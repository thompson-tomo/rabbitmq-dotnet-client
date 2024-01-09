using System.Diagnostics;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

const string queueName = "gh-1464";
const int messageCount = 1024;
int messagesReceived = 0;

var publishSyncSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var consumeConnectionShutdownSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

using var cts = new CancellationTokenSource();
cts.Token.Register(() =>
{
    Console.WriteLine("[INFO] CANCELLING PUBLISH SYNC SOURCE");
    publishSyncSource.SetCanceled();
});

Console.CancelKeyPress += delegate (object? sender, ConsoleCancelEventArgs e)
{
    e.Cancel = true;
    cts.Cancel();
};

var cf = new ConnectionFactory
{
    AutomaticRecoveryEnabled = true,
    TopologyRecoveryEnabled = true,
    Port = 55672,
    DispatchConsumersAsync = true
};

using IConnection consumeConnection = await cf.CreateConnectionAsync();
using IChannel consumeChannel = await consumeConnection.CreateChannelAsync();

consumeConnection.ConnectionShutdown += (o, ea) =>
{
    Console.WriteLine("[INFO] SAW CONSUME CONNECTION SHUTDOWN");
    if (cts.IsCancellationRequested)
    {
        Console.WriteLine("[INFO] CANCELING SHUTDOWN SYNC SOURCE");
        consumeConnectionShutdownSource.SetCanceled();
    }
};

consumeChannel.ChannelShutdown += (o, ea) =>
{
    Console.WriteLine("[INFO] SAW CONSUME CHANNEL SHUTDOWN");
};

var consumer = new AsyncEventingBasicConsumer(consumeChannel);

consumer.ConsumerCancelled += async (object sender, ConsumerEventArgs args) =>
{
    Debug.Assert(Object.ReferenceEquals(consumer, sender));
    Console.WriteLine("[INFO] SAW CONSUMER CANCELLED");
    await Task.Yield();
};

consumer.Registered += async (object sender, ConsumerEventArgs args) =>
{
    Debug.Assert(Object.ReferenceEquals(consumer, sender));
    Console.WriteLine("[INFO] SAW CONSUMER REGISTERED");
    await Task.Yield();
};

consumer.Received += async (object sender, BasicDeliverEventArgs args) =>
{
    Debug.Assert(Object.ReferenceEquals(consumer, sender));
    var c = sender as AsyncEventingBasicConsumer;
    Console.WriteLine($"[INFO] CONSUMER SAW TAG: {args.DeliveryTag}");
    await consumeChannel.BasicAckAsync(args.DeliveryTag, false);
    messagesReceived++;
    if (messagesReceived == messageCount)
    {
        publishSyncSource.SetResult(true);
    }
};

QueueDeclareOk q = await consumeChannel.QueueDeclareAsync(queue: queueName,
    passive: false, durable: false, exclusive: false, autoDelete: false, arguments: null);
Debug.Assert(queueName == q.QueueName);

await consumeChannel.BasicQosAsync(0, 1, false);
await consumeChannel.BasicConsumeAsync(queue: queueName, autoAck: false,
    consumerTag: string.Empty, noLocal: false, exclusive: false,
    arguments: null, consumer);

var publishTask = Task.Run(async () =>
{
    var publishConnectionFactory = new ConnectionFactory
    {
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true
    };

    using (IConnection publishConnection = await publishConnectionFactory.CreateConnectionAsync())
    {
        using (IChannel publishChannel = await publishConnection.CreateChannelAsync())
        {
            publishChannel.BasicAcks += (object? sender, BasicAckEventArgs e) =>
            {
                Console.WriteLine($"[INFO] PUBLISHER SAW ACK: {e.DeliveryTag}");
            };

            publishChannel.BasicNacks += (object? sender, BasicNackEventArgs e) =>
            {
                Console.WriteLine($"[INFO] PUBLISHER SAW NACK: {e.DeliveryTag}");
            };

            await publishChannel.ConfirmSelectAsync();

            Console.WriteLine($"[INFO] PUBLISHING MESSAGES: {messageCount}");
            for (int i = 0; i < messageCount; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                byte[] _body = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());
                await publishChannel.BasicPublishAsync(exchange: string.Empty, routingKey: queueName, mandatory: true, body: _body);
                await publishChannel.WaitForConfirmsOrDieAsync();
                await Task.Delay(TimeSpan.FromSeconds(1));
                Console.WriteLine($"[INFO] SENT MESSAGE: {i}");
            }
        }
    }
});

try
{
    await publishSyncSource.Task;
    Debug.Assert(messageCount == messagesReceived);
}
catch (OperationCanceledException ex)
{
    Console.WriteLine($"[INFO] CANCELLATION REQUESTED: {ex}");
    await consumeConnection.CloseAsync();

    try
    {
        await consumeConnectionShutdownSource.Task;
    }
    catch (OperationCanceledException ccssex)
    {
        Console.WriteLine($"[INFO] CONSUME CONNECTION SYNC SOURCE CANCELED: {ccssex}");
    }
}

try
{
    await publishTask;
}
catch (OperationCanceledException pubex)
{
    Console.WriteLine($"[INFO] PUBLISH TASK CANCELED: {pubex}");
}

Console.WriteLine($"[INFO] PUBLISH TASK COMPLETED");

Console.WriteLine($"[INFO] ALL TASKS COMPLETED");
