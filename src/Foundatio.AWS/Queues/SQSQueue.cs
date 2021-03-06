﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Foundatio.AsyncEx;
using ThirdParty.Json.LitJson;

namespace Foundatio.Queues {
    public class SQSQueue<T> : QueueBase<T, SQSQueueOptions<T>> where T : class {
        private readonly AsyncLock _lock = new AsyncLock();
        private readonly Lazy<AmazonSQSClient> _client;
        private string _queueUrl;
        private string _deadUrl;

        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;

        public SQSQueue(SQSQueueOptions<T> options) : base(options) {
            _client = new Lazy<AmazonSQSClient>(() => new AmazonSQSClient(options.Credentials ?? FallbackCredentialsFactory.GetCredentials(), options.RegionEndpoint));
        }

        protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = new CancellationToken()) {
            if (!String.IsNullOrEmpty(_queueUrl))
                return;

            using (await _lock.LockAsync(cancellationToken).AnyContext()) {
                if (!String.IsNullOrEmpty(_queueUrl))
                    return;

                try {
                    var urlResponse = await _client.Value.GetQueueUrlAsync(_options.Name, cancellationToken).AnyContext();
                    _queueUrl = urlResponse.QueueUrl;
                } catch (QueueDoesNotExistException) {
                    if (!_options.CanCreateQueue)
                        throw;
                }

                if (!String.IsNullOrEmpty(_queueUrl))
                    return;

                await CreateQueueAsync().AnyContext();
            }
        }

        protected override async Task<string> EnqueueImplAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            var message = new SendMessageRequest {
                QueueUrl = _queueUrl,
                MessageBody = await _serializer.SerializeToStringAsync(data).AnyContext(),
            };

            var response = await _client.Value.SendMessageAsync(message).AnyContext();

            Interlocked.Increment(ref _enqueuedCount);
            var entry = new QueueEntry<T>(response.MessageId, data, this, SystemClock.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();

            return response.MessageId;
        }

        protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken) {
            // sqs doesn't support already canceled token, change timeout and token for sqs pattern
            int waitTimeout = linkedCancellationToken.IsCancellationRequested ? 0 : (int)_options.ReadQueueTimeout.TotalSeconds;
            var cancel = linkedCancellationToken.IsCancellationRequested ? CancellationToken.None : linkedCancellationToken;

            var request = new ReceiveMessageRequest {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = 1,
                VisibilityTimeout = (int)_options.WorkItemTimeout.TotalSeconds,
                WaitTimeSeconds = waitTimeout,
                AttributeNames = new List<string> { "ApproximateReceiveCount", "SentTimestamp" }
            };

            // receive message local function
            async Task<ReceiveMessageResponse> receiveMessageAsync()
            {
                try {
                    return await _client.Value.ReceiveMessageAsync(request, cancel).AnyContext();
                } catch (OperationCanceledException) {
                    return null;
                }
            }

            var response = await receiveMessageAsync().AnyContext();
            // retry loop
            while (response == null && !linkedCancellationToken.IsCancellationRequested) {
                try {
                    await SystemClock.SleepAsync(_options.DequeueInterval, linkedCancellationToken).AnyContext();
                } catch (OperationCanceledException) { }

                response = await receiveMessageAsync().AnyContext();
            }

            if (response == null || response.Messages.Count == 0)
                return null;

            Interlocked.Increment(ref _dequeuedCount);

            var message = response.Messages.First();
            string body = message.Body;
            var data = await _serializer.DeserializeAsync<T>(body).AnyContext();
            var entry = new SQSQueueEntry<T>(message, data, this);

            await OnDequeuedAsync(entry).AnyContext();

            return entry;
        }

        public override async Task RenewLockAsync(IQueueEntry<T> queueEntry) {
            _logger.Debug("Queue {0} renew lock item: {1}", _options.Name, queueEntry.Id);

            var entry = ToQueueEntry(queueEntry);
            var request = new ChangeMessageVisibilityRequest {
                QueueUrl = _queueUrl,
                VisibilityTimeout = (int)_options.WorkItemTimeout.TotalSeconds,
                ReceiptHandle = entry.UnderlyingMessage.ReceiptHandle
            };

            await _client.Value.ChangeMessageVisibilityAsync(request).AnyContext();
            _logger.Trace("Renew lock done: {0}", queueEntry.Id);
        }

        public override async Task CompleteAsync(IQueueEntry<T> queueEntry) {
            _logger.Debug("Queue {0} complete item: {1}", _options.Name, queueEntry.Id);
            if (queueEntry.IsAbandoned || queueEntry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            var entry = ToQueueEntry(queueEntry);
            var request = new DeleteMessageRequest {
                QueueUrl = _queueUrl,
                ReceiptHandle = entry.UnderlyingMessage.ReceiptHandle,
            };

            await _client.Value.DeleteMessageAsync(request).AnyContext();

            Interlocked.Increment(ref _completedCount);
            queueEntry.MarkCompleted();
            await OnCompletedAsync(queueEntry).AnyContext();
            _logger.Trace("Complete done: {0}", queueEntry.Id);
        }

        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            _logger.Debug("Queue {_options.Name}:{QueueId} abandon item: {entryId}", _options.Name, QueueId, entry.Id);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            var sqsQueueEntry = ToQueueEntry(entry);
            // re-queue and wait for processing
            var request = new ChangeMessageVisibilityRequest {
                QueueUrl = _queueUrl,
                VisibilityTimeout = (int)_options.WorkItemTimeout.TotalSeconds,
                ReceiptHandle = sqsQueueEntry.UnderlyingMessage.ReceiptHandle,
            };

            // TODO: Ensure that we don't need to move this to a deadletter queue
            await _client.Value.ChangeMessageVisibilityAsync(request).AnyContext();

            Interlocked.Increment(ref _abandonedCount);
            entry.MarkAbandoned();

            await OnAbandonedAsync(sqsQueueEntry).AnyContext();
            _logger.Trace("Abandon complete: {entryId}", entry.Id);
        }

        protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken) {
            throw new NotImplementedException();
        }

        protected override async Task<QueueStats> GetQueueStatsImplAsync() {
            var attributeNames = new List<string> { QueueAttributeName.All };
            var queueRequest = new GetQueueAttributesRequest(_queueUrl, attributeNames);
            var queueAttributes = await _client.Value.GetQueueAttributesAsync(queueRequest).AnyContext();

            int queueCount = queueAttributes.ApproximateNumberOfMessages;
            int workingCount = queueAttributes.ApproximateNumberOfMessagesNotVisible;
            int deadCount = 0;

            // dead letter supported
            if (!_options.SupportDeadLetter) {
                return new QueueStats {
                    Queued = queueCount,
                    Working = workingCount,
                    Deadletter = deadCount,
                    Enqueued = _enqueuedCount,
                    Dequeued = _dequeuedCount,
                    Completed = _completedCount,
                    Abandoned = _abandonedCount,
                    Errors = _workerErrorCount,
                    Timeouts = 0
                };
            }

            // lookup dead letter url
            if (String.IsNullOrEmpty(_deadUrl)) {
                string deadLetterName = queueAttributes.Attributes.DeadLetterQueue();
                if (!String.IsNullOrEmpty(deadLetterName)) {
                    var deadResponse = await _client.Value.GetQueueUrlAsync(deadLetterName).AnyContext();
                    _deadUrl = deadResponse.QueueUrl;
                }
            }

            // get attributes from dead letter
            if (!String.IsNullOrEmpty(_deadUrl)) {
                var deadRequest = new GetQueueAttributesRequest(_deadUrl, attributeNames);
                var deadAttributes = await _client.Value.GetQueueAttributesAsync(deadRequest).AnyContext();
                deadCount = deadAttributes.ApproximateNumberOfMessages;
            }

            return new QueueStats {
                Queued = queueCount,
                Working = workingCount,
                Deadletter = deadCount,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = 0
            };
        }

        public override async Task DeleteQueueAsync() {
            if (!String.IsNullOrEmpty(_queueUrl)) {
                var response = await _client.Value.DeleteQueueAsync(_queueUrl).AnyContext();
            }
            if (!String.IsNullOrEmpty(_deadUrl)) {
                var response = await _client.Value.DeleteQueueAsync(_deadUrl).AnyContext();
            }

            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var linkedCancellationToken = GetLinkedDisposableCanncellationToken(cancellationToken);

            Task.Run(async () => {
                _logger.Trace("WorkerLoop Start {_options.Name}", _options.Name);

                while (!linkedCancellationToken.IsCancellationRequested) {
                    _logger.Trace("WorkerLoop Signaled {_options.Name}", _options.Name);

                    IQueueEntry<T> entry = null;
                    try {
                        entry = await DequeueImplAsync(linkedCancellationToken).AnyContext();
                    } catch (OperationCanceledException) { }

                    if (linkedCancellationToken.IsCancellationRequested || entry == null)
                        continue;

                    try {
                        await handler(entry, linkedCancellationToken).AnyContext();
                        if (autoComplete && !entry.IsAbandoned && !entry.IsCompleted)
                            await entry.CompleteAsync().AnyContext();
                    } catch (Exception ex) {
                        Interlocked.Increment(ref _workerErrorCount);
                        _logger.Error(ex, "Worker error: {0}", ex.Message);

                        if (entry != null && !entry.IsAbandoned && !entry.IsCompleted)
                            await entry.AbandonAsync().AnyContext();
                    }
                }

                _logger.Trace("Worker exiting: {0} Cancel Requested: {1}", _options.Name, linkedCancellationToken.IsCancellationRequested);
            }, linkedCancellationToken);
        }

        public override void Dispose() {
            base.Dispose();

            if (_client.IsValueCreated)
                _client.Value.Dispose();
        }

        protected virtual async Task CreateQueueAsync() {
            // step 1, create queue
            var createQueueRequest = new CreateQueueRequest { QueueName = _options.Name };
            var createQueueResponse = await _client.Value.CreateQueueAsync(createQueueRequest).AnyContext();
            _queueUrl = createQueueResponse.QueueUrl;

            if (!_options.SupportDeadLetter)
                return;

            // step 2, create dead letter queue
            var createDeadRequest = new CreateQueueRequest { QueueName = _options.Name + "-deadletter" };
            var createDeadResponse = await _client.Value.CreateQueueAsync(createDeadRequest).AnyContext();
            _deadUrl = createDeadResponse.QueueUrl;


            // step 3, get dead letter attributes
            var attributeNames = new List<string> { QueueAttributeName.QueueArn };
            var deadAttributeRequest = new GetQueueAttributesRequest(_deadUrl, attributeNames);
            var deadAttributeResponse = await _client.Value.GetQueueAttributesAsync(deadAttributeRequest).AnyContext();

            // step 4, set retry policy
            var redrivePolicy = new JsonData {
                ["maxReceiveCount"] = _options.Retries.ToString(),
                ["deadLetterTargetArn"] = deadAttributeResponse.QueueARN
            };

            var attributes = new Dictionary<string, string> {
                [QueueAttributeName.RedrivePolicy] = JsonMapper.ToJson(redrivePolicy)
            };

            var setAttributeRequest = new SetQueueAttributesRequest(_queueUrl, attributes);
            var setAttributeResponse = await _client.Value.SetQueueAttributesAsync(setAttributeRequest).AnyContext();
        }

        private static SQSQueueEntry<T> ToQueueEntry(IQueueEntry<T> entry) {
            var result = entry as SQSQueueEntry<T>;
            if (result == null)
                throw new ArgumentException($"Expected {nameof(SQSQueueEntry<T>)} but received unknown queue entry type {entry.GetType()}");

            return result;
        }
    }
}