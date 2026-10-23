// Pruebas del consumidor de EV-01 contra Kafka y PostgreSQL reales: que ningún
// fallo del broker ni de la base deje caer al servicio, que no se pierda ni se
// descarte en silencio ningún evento, y que lo que va a mensajes muertos sea
// solo lo que falla por sí mismo.

using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RedVital.Campanas.Api.Mensajeria;
using RedVital.Campanas.Aplicacion.CasosDeUso;
using RedVital.Campanas.Aplicacion.Eventos;
using RedVital.Campanas.Dominio;
using RedVital.Campanas.Infraestructura.Persistencia;
using Testcontainers.Kafka;
using Xunit;

namespace RedVital.Campanas.Pruebas;

/// <summary>Broker como el real: un tema que no está en el catálogo no existe.</summary>
public sealed class BrokerKafka : IAsyncLifetime
{
    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.3.1")
        .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false")
        .Build();

    public string Servidores => _kafka.GetBootstrapAddress();

    public async Task InitializeAsync()
    {
        await _kafka.StartAsync();
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Servidores }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = Temas.DonacionCompletada, NumPartitions = 3, ReplicationFactor = 1 }]);
    }

    public Task DisposeAsync() => _kafka.DisposeAsync().AsTask();
}

public class ConsumidorKafkaPruebas : IClassFixture<BaseCampanias>, IClassFixture<BrokerKafka>
{
    private const string TemaMuertos = Temas.DonacionCompletada + Temas.SufijoMensajesMuertos;

    private readonly BaseCampanias _base;
    private readonly BrokerKafka _broker;
    private volatile string _cadenaBase;

    public ConsumidorKafkaPruebas(BaseCampanias b, BrokerKafka k)
    {
        _base = b;
        _broker = k;
        _cadenaBase = b.CadenaServicio;
    }

    [Fact]
    public async Task Sin_tema_de_mensajes_muertos_el_consumidor_no_se_cae_ni_descarta_el_evento()
    {
        await BorrarTemaAsync(TemaMuertos);
        var campania = await CrearCampaniaAsync();
        var malFormado = Guid.NewGuid();
        var bueno = Guid.NewGuid();

        await using var consumidor = await IniciarConsumidorAsync();
        // Misma clave: misma partición, y el bueno queda detrás del mal formado.
        await PublicarAsync("clave-1", malFormado, "{esto no es json");
        await PublicarAsync("clave-1", bueno, $$"""{"donacion_id":"{{Guid.NewGuid()}}","campania_id":"{{campania}}"}""");

        // El broker no acepta el mensaje muerto: el consumidor no se cae ni lo salta.
        await Task.Delay(TimeSpan.FromSeconds(12));
        Assert.False(consumidor.Servicio.ExecuteTask!.IsCompleted, "el consumidor terminó");
        Assert.Equal(0, await DonacionesAsync(campania));

        // Cuando el tema existe, el mensaje llega a él y el que venía detrás se aplica.
        await CrearTemaAsync(TemaMuertos, 1);
        Assert.True(await LlegaAsync(TemaMuertos, malFormado, TimeSpan.FromSeconds(30)));
        await EsperarAsync(async () => await DonacionesAsync(campania) == 1);
    }

    [Fact]
    public async Task Con_la_base_caida_el_evento_espera_y_no_va_a_mensajes_muertos()
    {
        await CrearTemaAsync(TemaMuertos, 1);
        var campania = await CrearCampaniaAsync();
        var evento = Guid.NewGuid();

        await using var consumidor = await IniciarConsumidorAsync();
        _cadenaBase = new NpgsqlConnectionStringBuilder(_base.CadenaServicio) { Port = 1, Timeout = 2 }.ConnectionString; // nadie escucha ahí
        await PublicarAsync(campania.ToString(), evento, $$"""{"donacion_id":"{{Guid.NewGuid()}}","campania_id":"{{campania}}"}""");

        await Task.Delay(TimeSpan.FromSeconds(8));
        Assert.False(consumidor.Servicio.ExecuteTask!.IsCompleted, "el consumidor terminó");
        Assert.False(await LlegaAsync(TemaMuertos, evento, TimeSpan.FromSeconds(3)), "un evento sano fue a mensajes muertos");

        _cadenaBase = _base.CadenaServicio; // la base vuelve
        await EsperarAsync(async () => await DonacionesAsync(campania) == 1);
        Assert.False(await LlegaAsync(TemaMuertos, evento, TimeSpan.FromSeconds(3)));
    }

    // ------------------------------------------------------------------ utilidades

    private sealed class ConsumidorEnMarcha(ConsumidorDonacionCompletada servicio, ProductorKafka productor,
        ServiceProvider proveedor) : IAsyncDisposable
    {
        public ConsumidorDonacionCompletada Servicio { get; } = servicio;

        public async ValueTask DisposeAsync()
        {
            await Servicio.StopAsync(CancellationToken.None);
            productor.Dispose();
            await proveedor.DisposeAsync();
        }
    }

    private async Task<ConsumidorEnMarcha> IniciarConsumidorAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging(l => l.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        servicios.AddDbContext<CampanasDbContext>(o => o.UseNpgsql(_cadenaBase), optionsLifetime: ServiceLifetime.Scoped);
        servicios.AddScoped<AplicarDonacionCompletada>();
        var proveedor = servicios.BuildServiceProvider();

        var opciones = new OpcionesKafka
        {
            Habilitado = true, Servidores = _broker.Servidores, Protocolo = "Plaintext",
            EsperaDependencia = TimeSpan.FromMilliseconds(500),
        };
        var productor = new ProductorKafka(opciones);
        var servicio = new ConsumidorDonacionCompletada(opciones, productor,
            proveedor.GetRequiredService<IServiceScopeFactory>(),
            proveedor.GetRequiredService<ILogger<ConsumidorDonacionCompletada>>());
        await servicio.StartAsync(CancellationToken.None);
        return new ConsumidorEnMarcha(servicio, productor, proveedor);
    }

    private async Task<Guid> CrearCampaniaAsync()
    {
        await using var c = _base.Contexto();
        var campania = new Campania
        {
            Id = Guid.NewGuid(), InstitucionId = Guid.NewGuid(), TerritorioCodigo = "05001", TerritorioRuta = "/00/05/05001",
            Nombre = "Jornada", Sede = "Parque", IniciaEn = DateTimeOffset.UtcNow, TerminaEn = DateTimeOffset.UtcNow.AddDays(1),
            Estado = "publicada", PublicadaEn = DateTimeOffset.UtcNow,
            CreadaPor = Guid.NewGuid(), CreadaEn = DateTimeOffset.UtcNow, ActualizadaEn = DateTimeOffset.UtcNow,
        };
        c.Campanias.Add(campania);
        await c.SaveChangesAsync();
        return campania.Id;
    }

    private async Task<int> DonacionesAsync(Guid campania)
    {
        await using var c = _base.Contexto();
        return (await c.Campanias.AsNoTracking().SingleAsync(x => x.Id == campania)).DonacionesRegistradas;
    }

    private async Task PublicarAsync(string clave, Guid eventoId, string carga)
    {
        using var productor = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _broker.Servidores }).Build();
        await productor.ProduceAsync(Temas.DonacionCompletada, new Message<string, string>
        {
            Key = clave,
            Value = carga,
            Headers = new Headers
            {
                { "evento-id", Encoding.UTF8.GetBytes(eventoId.ToString()) },
                { "evento-tipo", Encoding.UTF8.GetBytes(DonacionCompletadaV1.Tipo) },
                { "productor", Encoding.UTF8.GetBytes("donacion") },
            },
        });
    }

    /// <summary>Lee el tema desde el principio durante la ventana dada y dice si apareció el evento.</summary>
    private async Task<bool> LlegaAsync(string tema, Guid eventoId, TimeSpan ventana)
    {
        using var lector = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _broker.Servidores, GroupId = "prueba-" + Guid.NewGuid(),
            AutoOffsetReset = AutoOffsetReset.Earliest, EnableAutoCommit = false,
        }).Build();
        lector.Subscribe(tema);
        var limite = DateTime.UtcNow + ventana;
        try
        {
            while (DateTime.UtcNow < limite)
            {
                ConsumeResult<string, string>? r;
                try
                {
                    r = lector.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException)
                {
                    await Task.Delay(200);
                    continue;
                }
                var cabecera = r?.Message.Headers.FirstOrDefault(h => h.Key == "evento-id");
                if (cabecera is not null && Encoding.UTF8.GetString(cabecera.GetValueBytes()) == eventoId.ToString())
                    return true;
            }
            return false;
        }
        finally
        {
            lector.Close();
        }
    }

    private async Task CrearTemaAsync(string tema, int particiones)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _broker.Servidores }).Build();
        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = tema, NumPartitions = particiones, ReplicationFactor = 1 }]);
        }
        catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
        }
    }

    private async Task BorrarTemaAsync(string tema)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _broker.Servidores }).Build();
        try
        {
            await admin.DeleteTopicsAsync([tema]);
        }
        catch (DeleteTopicsException e) when (e.Results.All(r => r.Error.Code == ErrorCode.UnknownTopicOrPart))
        {
        }
    }

    private static async Task EsperarAsync(Func<Task<bool>> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            if (await condicion()) return;
            await Task.Delay(250);
        }
        Assert.Fail("La condición no se cumplió en 30 segundos");
    }
}
