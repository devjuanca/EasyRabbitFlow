using RabbitMQ.Client;
using System;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Represents the configuration settings for connecting to a RabbitMQ host.
    /// This class provides various options to specify the RabbitMQ server details, including hostname, port, 
    /// authentication credentials, virtual host, and connection recovery behavior.
    /// </summary>
    public class HostSettings
    {
        /// <summary>
        /// Gets or sets the hostname or IP address of the RabbitMQ host.
        /// This is the address where the RabbitMQ server is running. The default value is <see langword="localhost"/>.
        /// </summary>
        public string Host { get; set; } = "localhost";

        /// <summary>
        /// Gets or sets the port number for connecting to the RabbitMQ host.
        /// This is the port on which the RabbitMQ server is listening. 
        /// The default value is <see langword="5672"/>, 
        /// which is the standard port for RabbitMQ as specified by the AMQP protocol.
        /// </summary>
        public int Port { get; set; } = Protocols.DefaultProtocol.DefaultPort;

        /// <summary>
        /// Gets or sets the username used for authenticating with the RabbitMQ host.
        /// This is the username that will be used to log in to the RabbitMQ server.
        /// The default value is <see langword="guest"/>.
        /// </summary>
        public string Username { get; set; } = "guest";

        /// <summary>
        /// Gets or sets the password used for authenticating with the RabbitMQ host.
        /// This is the password that corresponds to the specified username for logging in to the RabbitMQ server. 
        /// The default value is <see langword="guest"/>.
        /// </summary>
        public string Password { get; set; } = "guest";

        /// <summary>
        /// Gets or sets the virtual host to connect to on the RabbitMQ host.
        /// A virtual host in RabbitMQ is a logical grouping of resources like exchanges, queues, and bindings. 
        /// The default value is <see langword="/"/>, which is the default virtual host in RabbitMQ.
        /// </summary>
        public string VirtualHost { get; set; } = "/";

        /// <summary>
        /// Gets or sets a value indicating whether EasyRabbitFlow's own recovery is enabled for consumers.
        /// </summary>
        /// <remarks>
        /// EasyRabbitFlow manages recovery itself: when a consumer's connection or channel shuts down unexpectedly,
        /// it re-establishes the connection and channel and <b>re-declares its full topology</b> (queue, exchange,
        /// bindings, dead-letter resources) before resuming consumption. Because of this, the RabbitMQ client's
        /// built-in automatic/topology recovery is intentionally left disabled — running both at once causes the
        /// client to re-bind/re-consume a recorded topology that does not include the consumer's main queue,
        /// surfacing as <c>404 NOT_FOUND</c> on reconnect.
        /// <para>
        /// When <see langword="true"/> (the default), a dropped consumer keeps retrying in the background with an
        /// exponential backoff seeded by <see cref="NetworkRecoveryInterval"/>. When <see langword="false"/>, a
        /// dropped consumer stays down until the application is restarted.
        /// </para>
        /// </remarks>
        public bool AutomaticRecoveryEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the base interval used by EasyRabbitFlow's own consumer recovery loop.
        /// </summary>
        /// <remarks>
        /// This is the delay before the first recovery attempt; subsequent attempts back off exponentially from this
        /// value (capped at 30 seconds, or this value when larger). Only meaningful when
        /// <see cref="AutomaticRecoveryEnabled"/> is <see langword="true"/>.
        /// The default value is <see langword="10"/> seconds.
        /// </remarks>
        public TimeSpan NetworkRecoveryInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the interval at which heartbeat signals are requested from the connection.
        /// </summary>
        /// <remarks>
        /// Adjusting this value can affect connection monitoring and responsiveness. A shorter
        /// interval may provide faster detection of connection issues but can increase network traffic.
        /// The default value is <see langword="30"/> seconds.
        /// </remarks>
        public TimeSpan RequestedHeartbeat { get; set; } = TimeSpan.FromSeconds(30);
    }
}
