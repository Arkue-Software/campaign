// RedVital — campaign-service. Consumidor de EV-01 donation.completed, grupo
// campanias. Consumo idempotente (ADR-018): la posición se confirma después de
// aplicar el evento. Tres reintentos con espera creciente; si persiste el fallo,
// o si el mensaje no cumple el contrato, va a <tema>.campanias.dlt.

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using RedVital.Campanas.Aplicacion.CasosDeUso;
using RedVital.Campanas.Aplicacion.Eventos;

namespace RedVital.Campanas.Api.Mensajeria;

public sealed class ConsumidorDonacionCompletada : BackgroundService
{
    private static readonly TimeSpan[] Esperas = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

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

    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(() => BucleAsync(ct), ct);

    private async Task BucleAsync(CancellationToken ct)
    {
        using var consumidor = new ConsumerBuilder<string, string>(_opciones.Aplicar(new ConsumerConfig
        {
            GroupId = Temas.Grupo,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            ClientId = "campanias",
        })).Build();
        consumidor.Subscribe(Temas.DonacionCompletada);

        while (!ct.IsCancellationRequested)
        {
            ConsumeResult<string, string>? registro;
            try
            {
                registro = consumidor.Consume(TimeSpan.FromSeconds(1));
            }
            catch (ConsumeException e)
            {
                _log.LogWarning("No fue posible leer del broker: {Error}", e.Error.Reason);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            if (registro is null) continue;

            await ProcesarAsync(registro, ct);
            consumidor.Commit(registro);
        }
        consumidor.Close();
    }

    private async Task ProcesarAsync(ConsumeResult<string, string> registro, CancellationToken ct)
    {
        if (!Leer(registro, out var eventoId, out var evento))
        {
            await MensajeMuertoAsync(registro, "El mensaje no cumple el contrato de donation.completed.", ct);
            return;
        }

        for (var intento = 0; ; intento++)
        {
            try
            {
                using var ambito = _ambitos.CreateScope();
                var aplicado = await ambito.ServiceProvider.GetRequiredService<AplicarDonacionCompletada>()
                    .EjecutarAsync(eventoId, evento!, ct);
                _log.LogInformation("Evento {Id} de donación {Donacion}: {Resultado}", eventoId, evento!.DonacionId,
                    aplicado ? "aplicado" : "duplicado");
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException && intento < Esperas.Length)
            {
                _log.LogWarning("Falló la aplicación del evento {Id} (intento {N}): {Mensaje}", eventoId, intento + 1, e.Message);
                await Task.Delay(Esperas[intento], ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await MensajeMuertoAsync(registro, e.Message, ct);
                return;
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

    private async Task MensajeMuertoAsync(ConsumeResult<string, string> r, string motivo, CancellationToken ct)
    {
        var cabeceras = new Headers();
        foreach (var h in r.Message.Headers ?? new Headers()) cabeceras.Add(h.Key, h.GetValueBytes());
        cabeceras.Add("dlt-motivo", Encoding.UTF8.GetBytes(motivo.Length > 200 ? motivo[..200] : motivo));
        cabeceras.Add("dlt-origen", Encoding.UTF8.GetBytes($"{r.Topic}/{r.Partition.Value}/{r.Offset.Value}"));
        await _kafka.Productor.ProduceAsync(r.Topic + Temas.SufijoMensajesMuertos,
            new Message<string, string> { Key = r.Message.Key, Value = r.Message.Value, Headers = cabeceras }, ct);
        _log.LogError("Evento enviado a mensajes muertos desde {Origen}: {Motivo}", r.TopicPartitionOffset, motivo);
    }
}
