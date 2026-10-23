// RedVital — campaign-service. Consumidor de EV-01 donation.completed, grupo
// campanias (ADR-018, DD 14.1). Ni deja caer al servicio ni se salta un evento:
//
//  - Al menos una vez sin esperar al broker por cada mensaje: la posición se
//    guarda solo después de aplicar el evento y librdkafka la confirma en
//    segundo plano. Un duplicado tras un reinicio lo descarta evento_procesado.
//  - Fallo del mensaje: tres reintentos en pocos segundos (1, 2 y 4) y luego
//    <tema>.campanias.dlt. Un mensaje que no cumple el contrato va directo.
//  - Fallo de la base: no es culpa del evento. Se vuelve a leer el mismo
//    mensaje cada EsperaDependencia hasta que la base responda; nada va a
//    mensajes muertos por eso.
//  - Fallo del broker (no se pudo leer, guardar la posición o escribir en
//    mensajes muertos): se registra y se reintenta. El API sigue atendiendo, y
//    si el consumidor mismo queda inservible se crea uno nuevo.

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using RedVital.Campanas.Aplicacion.CasosDeUso;
using RedVital.Campanas.Aplicacion.Eventos;
using RedVital.Campanas.Infraestructura.Persistencia;

namespace RedVital.Campanas.Api.Mensajeria;

public sealed class ConsumidorDonacionCompletada : BackgroundService
{
    private static readonly TimeSpan[] Esperas = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly OpcionesKafka _opciones;
    private readonly ProductorKafka _kafka;
    private readonly IServiceScopeFactory _ambitos;
    private readonly ILogger<ConsumidorDonacionCompletada> _log;

    public ConsumidorDonacionCompletada(OpcionesKafka opciones, ProductorKafka kafka, IServiceScopeFactory ambitos,
        ILogger<ConsumidorDonacionCompletada> log)
    {
        _opciones = opciones;
        _kafka = kafka;
        _ambitos = ambitos;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(() => SupervisarAsync(ct), ct);

    /// <summary>Si el consumidor falla de forma inesperada, se crea otro: nunca se detiene el servicio.</summary>
    private async Task SupervisarAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumirAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogError(e, "El consumidor de {Tema} falló; se crea otro en {Espera}", Temas.DonacionCompletada,
                    _opciones.EsperaDependencia);
                await EsperarAsync(_opciones.EsperaDependencia, ct);
            }
        }
    }

    private async Task ConsumirAsync(CancellationToken ct)
    {
        using var consumidor = new ConsumerBuilder<string, string>(_opciones.Aplicar(new ConsumerConfig
        {
            GroupId = Temas.Grupo,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            AutoCommitIntervalMs = 1000,
            ClientId = "campanias",
        }))
            .SetErrorHandler((_, e) => _log.LogWarning("Kafka: {Motivo}", e.Reason))
            .Build();
        consumidor.Subscribe(Temas.DonacionCompletada);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, string>? registro;
                try
                {
                    registro = consumidor.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException e) when (!e.Error.IsFatal)
                {
                    _log.LogWarning("No fue posible leer del broker: {Error}", e.Error.Reason);
                    await EsperarAsync(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                if (registro is null) continue;

                if (await ProcesarAsync(registro, ct))
                    GuardarPosicion(consumidor, registro);
                else
                    VolverALeer(consumidor, registro);
            }
        }
        finally
        {
            try
            {
                consumidor.Close(); // confirma lo ya guardado y deja el grupo
            }
            catch (KafkaException e)
            {
                _log.LogWarning("No fue posible cerrar el consumidor limpiamente: {Error}", e.Error.Reason);
            }
        }
    }

    /// <returns>Verdadero si el evento quedó resuelto: aplicado, duplicado o en mensajes muertos.</returns>
    private async Task<bool> ProcesarAsync(ConsumeResult<string, string> registro, CancellationToken ct)
    {
        if (!Leer(registro, out var eventoId, out var evento))
            return await MensajeMuertoAsync(registro, "El mensaje no cumple el contrato de donation.completed.", ct);

        for (var intento = 0; ; intento++)
        {
            try
            {
                using var ambito = _ambitos.CreateScope();
                var aplicado = await ambito.ServiceProvider.GetRequiredService<AplicarDonacionCompletada>()
                    .EjecutarAsync(eventoId, evento!, ct);
                _log.LogInformation("Evento {Id} de donación {Donacion}: {Resultado}", eventoId, evento!.DonacionId,
                    aplicado ? "aplicado" : "duplicado");
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException && FallosTransitorios.EsDeLaBase(e))
            {
                _log.LogWarning("La base no responde; el evento {Id} espera sin pasar a mensajes muertos: {Mensaje}",
                    eventoId, e.Message);
                await EsperarAsync(_opciones.EsperaDependencia, ct);
                return false;
            }
            catch (Exception e) when (e is not OperationCanceledException && intento < Esperas.Length)
            {
                _log.LogWarning("Falló la aplicación del evento {Id} (intento {N}): {Mensaje}", eventoId, intento + 1, e.Message);
                await EsperarAsync(Esperas[intento], ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return await MensajeMuertoAsync(registro, e.Message, ct);
            }
        }
    }

    private static bool Leer(ConsumeResult<string, string> r, out Guid eventoId, out DonacionCompletadaV1? evento)
    {
        evento = null;
        eventoId = Guid.Empty;
        var cabecera = r.Message.Headers?.FirstOrDefault(h => h.Key == "evento-id");
        if (cabecera is null || !Guid.TryParse(Encoding.UTF8.GetString(cabecera.GetValueBytes()), out eventoId))
            return false;
        try
        {
            evento = JsonSerializer.Deserialize<DonacionCompletadaV1>(r.Message.Value, Json.Contrato);
            return evento is not null && evento.DonacionId != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <returns>Verdadero si el mensaje quedó en el tema de mensajes muertos; si el broker no lo aceptó, falso.</returns>
    private async Task<bool> MensajeMuertoAsync(ConsumeResult<string, string> r, string motivo, CancellationToken ct)
    {
        var cabeceras = new Headers();
        foreach (var h in r.Message.Headers ?? new Headers()) cabeceras.Add(h.Key, h.GetValueBytes());
        cabeceras.Add("dlt-motivo", Encoding.UTF8.GetBytes(motivo.Length > 200 ? motivo[..200] : motivo));
        cabeceras.Add("dlt-origen", Encoding.UTF8.GetBytes($"{r.Topic}/{r.Partition.Value}/{r.Offset.Value}"));
        try
        {
            await _kafka.Productor.ProduceAsync(r.Topic + Temas.SufijoMensajesMuertos,
                new Message<string, string> { Key = r.Message.Key, Value = r.Message.Value, Headers = cabeceras }, ct);
        }
        catch (KafkaException e)
        {
            // Nunca se descarta en silencio: sin mensajes muertos, el evento se vuelve a leer después.
            _log.LogError("No fue posible escribir en mensajes muertos el evento de {Origen}; se reintentará: {Error}",
                r.TopicPartitionOffset, e.Error.Reason);
            await EsperarAsync(_opciones.EsperaDependencia, ct);
            return false;
        }
        _log.LogError("Evento enviado a mensajes muertos desde {Origen}: {Motivo}", r.TopicPartitionOffset, motivo);
        return true;
    }

    private void GuardarPosicion(IConsumer<string, string> consumidor, ConsumeResult<string, string> registro)
    {
        try
        {
            consumidor.StoreOffset(registro);
        }
        catch (KafkaException e)
        {
            // La partición se reasignó mientras se procesaba: su nuevo dueño lo relee desde
            // la última posición confirmada y evento_procesado descarta el duplicado.
            _log.LogWarning("No fue posible guardar la posición de {Origen}: {Error}", registro.TopicPartitionOffset, e.Error.Reason);
        }
    }

    private void VolverALeer(IConsumer<string, string> consumidor, ConsumeResult<string, string> registro)
    {
        try
        {
            consumidor.Seek(registro.TopicPartitionOffset);
        }
        catch (KafkaException e)
        {
            _log.LogWarning("No fue posible volver a {Origen}: {Error}", registro.TopicPartitionOffset, e.Error.Reason);
        }
    }

    /// <summary>Espera interrumpible: al apagar el servicio termina enseguida, y el ciclo ve la cancelación.</summary>
    private static async Task EsperarAsync(TimeSpan espera, CancellationToken ct)
    {
        try
        {
            await Task.Delay(espera, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
