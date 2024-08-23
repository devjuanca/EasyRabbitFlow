using RabbitMQ.Client;
namespace RabbitFlow.Settings
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
        /// This is the address where the RabbitMQ server is running. The default value is "localhost".
        /// </summary>
        public string Host { get; set; } = "localhost";

        /// <summary>
        /// Gets or sets the port number for connecting to the RabbitMQ host.
        /// This is the port on which the RabbitMQ server is listening. The default value is 5672, 
        /// which is the standard port for RabbitMQ as specified by the AMQP protocol.
        /// </summary>
        public int Port { get; set; } = Protocols.DefaultProtocol.DefaultPort;

        /// <summary>
        /// Gets or sets the username used for authenticating with the RabbitMQ host.
        /// This is the username that will be used to log in to the RabbitMQ server. The default value is "guest".
        /// </summary>
        public string Username { get; set; } = "guest";

        /// <summary>
        /// Gets or sets the password used for authenticating with the RabbitMQ host.
        /// This is the password that corresponds to the specified username for logging in to the RabbitMQ server. The default value is "guest".
        /// </summary>
        public string Password { get; set; } = "guest";

        /// <summary>
        /// Gets or sets the virtual host to connect to on the RabbitMQ host.
        /// A virtual host in RabbitMQ is a logical grouping of resources like exchanges, queues, and bindings. 
        /// The default value is "/", which is the default virtual host in RabbitMQ.
        /// </summary>
        public string VirtualHost { get; set; } = "/";

        /// <summary>
        /// Gets or sets a value indicating whether automatic recovery is enabled.
        /// When enabled, the client will automatically attempt to recover from network failures or other issues 
        /// that cause the connection to drop. The default value is <c>true</c>.
        /// </summary>
        public bool AutomaticRecoveryEnabled { get; set; } = true;
    }
}
