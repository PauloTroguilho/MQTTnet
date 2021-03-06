﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Core.Client;
using MQTTnet.Core.Exceptions;
using MQTTnet.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace MQTTnet.Core.ManagedClient
{
    public class ManagedMqttClient : IManagedMqttClient
    {
        private readonly ManagedMqttClientStorageManager _storageManager = new ManagedMqttClientStorageManager();
        private readonly BlockingCollection<MqttApplicationMessage> _messageQueue = new BlockingCollection<MqttApplicationMessage>();
        private readonly HashSet<TopicFilter> _subscriptions = new HashSet<TopicFilter>();

        private readonly IMqttClient _mqttClient;
        private readonly ILogger<ManagedMqttClient> _logger;

        private CancellationTokenSource _connectionCancellationToken;
        private CancellationTokenSource _publishingCancellationToken;

        private IManagedMqttClientOptions _options;
        private bool _subscriptionsNotPushed;
        
        public ManagedMqttClient(ILogger<ManagedMqttClient> logger, IMqttClient mqttClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mqttClient = mqttClient ?? throw new ArgumentNullException(nameof(mqttClient));

            _mqttClient.Connected += OnConnected;
            _mqttClient.Disconnected += OnDisconnected;
            _mqttClient.ApplicationMessageReceived += OnApplicationMessageReceived;
        }

        public bool IsConnected => _mqttClient.IsConnected;

        public event EventHandler<MqttClientConnectedEventArgs> Connected;
        public event EventHandler<MqttClientDisconnectedEventArgs> Disconnected;
        public event EventHandler<MqttApplicationMessageReceivedEventArgs> ApplicationMessageReceived;

        public async Task StartAsync(IManagedMqttClientOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.ClientOptions == null) throw new ArgumentException("The client options are not set.", nameof(options));

            if (!options.ClientOptions.CleanSession)
            {
                throw new NotSupportedException("The managed client does not support existing sessions.");
            }

            if (_connectionCancellationToken != null)
            {
                throw new InvalidOperationException("The managed client is already started.");
            }

            _options = options;
            await _storageManager.SetStorageAsync(_options.Storage).ConfigureAwait(false);

            if (_options.Storage != null)
            {
                var loadedMessages = await _options.Storage.LoadQueuedMessagesAsync().ConfigureAwait(false);
                foreach (var loadedMessage in loadedMessages)
                {
                    _messageQueue.Add(loadedMessage);    
                }
            }

            _connectionCancellationToken = new CancellationTokenSource();
            
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Factory.StartNew(() => MaintainConnectionAsync(_connectionCancellationToken.Token), _connectionCancellationToken.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

            _logger.LogInformation("Started");
        }

        public Task StopAsync()
        {
            _connectionCancellationToken?.Cancel(false);
            _connectionCancellationToken = null;

            while (_messageQueue.Any())
            {
                _messageQueue.Take();
            }

            return Task.FromResult(0);
        }

        public async Task PublishAsync(IEnumerable<MqttApplicationMessage> applicationMessages)
        {
            if (applicationMessages == null) throw new ArgumentNullException(nameof(applicationMessages));

            foreach (var applicationMessage in applicationMessages)
            {
                await _storageManager.AddAsync(applicationMessage).ConfigureAwait(false);
                _messageQueue.Add(applicationMessage);
            }
        }

        public Task SubscribeAsync(IEnumerable<TopicFilter> topicFilters)
        {
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            lock (_subscriptions)
            {
                foreach (var topicFilter in topicFilters)
                {
                    if (_subscriptions.Add(topicFilter))
                    {
                        _subscriptionsNotPushed = true;
                    }
                }
            }

            return Task.FromResult(0);
        }

        public Task UnsubscribeAsync(IEnumerable<TopicFilter> topicFilters)
        {
            lock (_subscriptions)
            {
                foreach (var topicFilter in topicFilters)
                {
                    if (_subscriptions.Remove(topicFilter))
                    {
                        _subscriptionsNotPushed = true;
                    }
                }
            }

            return Task.FromResult(0);
        }

        private async Task MaintainConnectionAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var connectionState = await ReconnectIfRequiredAsync().ConfigureAwait(false);
                    if (connectionState == ReconnectionResult.NotConnected)
                    {
                        _publishingCancellationToken?.Cancel(false);
                        _publishingCancellationToken = null;

                        await Task.Delay(_options.AutoReconnectDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (connectionState == ReconnectionResult.Reconnected || _subscriptionsNotPushed)
                    {
                        await PushSubscriptionsAsync();

                        _publishingCancellationToken = new CancellationTokenSource();

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                        Task.Factory.StartNew(() => PublishQueuedMessagesAsync(_publishingCancellationToken.Token), _publishingCancellationToken.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

                        continue;
                    }

                    if (connectionState == ReconnectionResult.StillConnected)
                    {
                        await Task.Delay(100, _connectionCancellationToken.Token).ConfigureAwait(false); // Consider using the _Disconnected_ event here. (TaskCompletionSource)
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (MqttCommunicationException exception)
            {
                _logger.LogWarning(new EventId(), exception, "Communication exception while maintaining connection.");
            }
            catch (Exception exception)
            {
                _logger.LogError(new EventId(), exception, "Unhandled exception while maintaining connection.");
            }
            finally
            {
                await _mqttClient.DisconnectAsync().ConfigureAwait(false);
                _logger.LogInformation("Stopped");
            }
        }

        private async Task PublishQueuedMessagesAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = _messageQueue.Take(cancellationToken);
                    if (message == null)
                    {
                        continue;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }

                    await TryPublishQueuedMessageAsync(message).ConfigureAwait(false);
                    await _storageManager.RemoveAsync(message).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _logger.LogInformation("Stopped publishing messages");
            }
        }

        private async Task TryPublishQueuedMessageAsync(MqttApplicationMessage message)
        {
            try
            {
                await _mqttClient.PublishAsync(message).ConfigureAwait(false);
            }
            catch (MqttCommunicationException exception)
            {
                _logger.LogWarning(new EventId(), exception, "Publishing application message failed.");

                if (message.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
                {
                    _messageQueue.Add(message);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(new EventId(), exception, "Unhandled exception while publishing queued application message.");
            }
        }

        private async Task PushSubscriptionsAsync()
        {
            _logger.LogInformation(nameof(ManagedMqttClient), "Synchronizing subscriptions");

            List<TopicFilter> subscriptions;
            lock (_subscriptions)
            {
                subscriptions = _subscriptions.ToList();
                _subscriptionsNotPushed = false;
            }

            if (!_subscriptions.Any())
            {
                return;
            }

            try
            {
                await _mqttClient.SubscribeAsync(subscriptions).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(new EventId(), exception, "Synchronizing subscriptions failed");
                _subscriptionsNotPushed = true;
            }
        }

        private async Task<ReconnectionResult> ReconnectIfRequiredAsync()
        {
            if (_mqttClient.IsConnected)
            {
                return ReconnectionResult.StillConnected;
            }

            try
            {
                await _mqttClient.ConnectAsync(_options.ClientOptions).ConfigureAwait(false);
                return ReconnectionResult.Reconnected;
            }
            catch (Exception)
            {
                return ReconnectionResult.NotConnected;
            }
        }

        private void OnApplicationMessageReceived(object sender, MqttApplicationMessageReceivedEventArgs eventArgs)
        {
            ApplicationMessageReceived?.Invoke(this, eventArgs);
        }

        private void OnDisconnected(object sender, MqttClientDisconnectedEventArgs eventArgs)
        {
            Disconnected?.Invoke(this, eventArgs);
        }

        private void OnConnected(object sender, MqttClientConnectedEventArgs eventArgs)
        {
            Connected?.Invoke(this, eventArgs);
        }
    }
}
