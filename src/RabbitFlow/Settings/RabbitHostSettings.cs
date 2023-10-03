using RabbitMQ.Client;
namespace RabbitFlow.Settings
{

    public class HostSettings
    {
        /// <summary>
        /// Gets or sets the hostname or IP address of the RabbitMQ host. Default is "localhost".
        /// </summary>
        public string Host { get; set; } = "localhost";

        /// <summary>
        /// Gets or sets the port number for connecting to the RabbitMQ host. Default is 5672.
        /// </summary>
        public int Port { get; set; } = Protocols.DefaultProtocol.DefaultPort;

        /// <summary>
        /// Gets or sets the username used for authenticating with the RabbitMQ host. Default is "guest".
        /// </summary>
        public string Username { get; set; } = "guest";

        /// <summary>
        /// Gets or sets the password used for authenticating with the RabbitMQ host. Default is "guest".
        /// </summary>
        public string Password { get; set; } = "guest";

        /// <summary>
        /// Gets or sets the virtual host to connect to on the RabbitMQ host. Default is "/".
        /// </summary>
        public string VirtualHost { get; set; } = "/";

        /// <summary>
        /// Gets or sets a value indicating whether automatic recovery is enabled. Default is true.
        /// </summary>
        public bool AutomaticRecoveryEnabled { get; set; } = true;
    }
}
