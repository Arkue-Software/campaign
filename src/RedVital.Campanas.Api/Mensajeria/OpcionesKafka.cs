// RedVital — campaign-service. Conexión al broker (Herramientas V4.0, entrada 83:
// Confluent.Kafka sobre librdkafka). Un usuario SCRAM propio, con las ACL de la
// Tabla 30 del DD: escribe campaign.published y lee donation.completed.

using Confluent.Kafka;

namespace RedVital.Campanas.Api.Mensajeria;

public sealed class OpcionesKafka
{
    public bool Habilitado { get; set; }
    public string Servidores { get; set; } = "kafka:9092";
    public string Usuario { get; set; } = "campanias";
    public string? Clave { get; set; }
    public string Protocolo { get; set; } = "SaslPlaintext";

    public T Aplicar<T>(T config) where T : ClientConfig
    {
        config.BootstrapServers = Servidores;
        config.SecurityProtocol = Enum.Parse<SecurityProtocol>(Protocolo, ignoreCase: true);
        if (config.SecurityProtocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl)
        {
            config.SaslMechanism = SaslMechanism.ScramSha512;
            config.SaslUsername = Usuario;
            config.SaslPassword = Clave;
        }
        return config;
    }
}

/// <summary>
/// Productor único del servicio: idempotente y con confirmación de todas las
/// réplicas. Lo usan el publicador de la bandeja y el envío a mensajes muertos.
/// </summary>
public sealed class ProductorKafka : IDisposable
{
    public IProducer<string, string> Productor { get; }

    public ProductorKafka(OpcionesKafka opciones) =>
        Productor = new ProducerBuilder<string, string>(opciones.Aplicar(new ProducerConfig
        {
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = 10_000,
            ClientId = "campanias",
        })).Build();

    public void Dispose()
    {
        Productor.Flush(TimeSpan.FromSeconds(5));
        Productor.Dispose();
    }
}
