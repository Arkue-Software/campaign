// Pruebas de integración de los eventos de Campañas contra PostgreSQL real,
// con los roles y las migraciones del repositorio de bases. El servicio se
// conecta como campana_servicio, así que también prueban los permisos.

using Microsoft.EntityFrameworkCore;
using Npgsql;
using RedVital.Campanas.Api.Mantenimiento;
using RedVital.Campanas.Aplicacion.CasosDeUso;
using RedVital.Campanas.Aplicacion.Eventos;
using RedVital.Campanas.Dominio;
using RedVital.Campanas.Infraestructura.Persistencia;
using Testcontainers.PostgreSql;
using Xunit;

namespace RedVital.Campanas.Pruebas;

public sealed class BaseCampanias : IAsyncLifetime
{
    public const string ClaveServicio = "servicio-prueba";
    private const string ClavePropietario = "propietario-prueba";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("db_campana").Build();

    public string CadenaServicio { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var dir = Path.GetFullPath(Environment.GetEnvironmentVariable("REDVITAL_MIGRACIONES_CAMPANAS")
            ?? Path.Combine(AppContext.BaseDirectory, "../../../../../../../../BDs_RedVital/databases/campanas"));
        await _postgres.CopyAsync(await File.ReadAllBytesAsync(Path.Combine(dir, "00_roles.sql")), "/tmp/00_roles.sql");
        var roles = await _postgres.ExecAsync(["psql", "-U", "postgres", "-d", "db_campana",
            "-v", $"password_propietario={ClavePropietario}", "-v", $"password_servicio={ClaveServicio}", "-f", "/tmp/00_roles.sql"]);
        Assert.True(roles.ExitCode == 0, roles.Stderr);

        var propietario = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
            { Username = "campana_propietario", Password = ClavePropietario }.ConnectionString;
        await using (var conexion = new NpgsqlConnection(propietario))
        {
            await conexion.OpenAsync();
            foreach (var archivo in Directory.GetFiles(Path.Combine(dir, "migraciones"), "V*.sql").Order())
            {
                await using var comando = new NpgsqlCommand(await File.ReadAllTextAsync(archivo), conexion);
                await comando.ExecuteNonQueryAsync();
            }
        }
        CadenaServicio = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
            { Username = "campana_servicio", Password = ClaveServicio }.ConnectionString;
    }

    public CampanasDbContext Contexto() =>
        new(new DbContextOptionsBuilder<CampanasDbContext>().UseNpgsql(CadenaServicio).Options);

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

public class EventosPostgresPruebas : IClassFixture<BaseCampanias>
{
    private readonly BaseCampanias _base;

    public EventosPostgresPruebas(BaseCampanias b) => _base = b;

    private async Task<Campania> CrearAsync(string estado = "borrador")
    {
        await using var c = _base.Contexto();
        var campania = new Campania
        {
            Id = Guid.NewGuid(), InstitucionId = Guid.NewGuid(), TerritorioCodigo = "05001", TerritorioRuta = "/00/05/05001",
            Nombre = "Jornada", Sede = "Parque", IniciaEn = DateTimeOffset.UtcNow.AddDays(1), TerminaEn = DateTimeOffset.UtcNow.AddDays(2),
            Estado = estado, PublicadaEn = estado == "borrador" ? null : DateTimeOffset.UtcNow,
            CreadaPor = Guid.NewGuid(), CreadaEn = DateTimeOffset.UtcNow, ActualizadaEn = DateTimeOffset.UtcNow,
        };
        c.Campanias.Add(campania);
        await c.SaveChangesAsync();
        return campania;
    }

    [Fact]
    public async Task La_publicacion_y_su_evento_se_guardan_juntos_con_los_permisos_del_servicio()
    {
        var campania = await CrearAsync();
        await using (var c = _base.Contexto())
        {
            var admin = new Solicitante(Guid.NewGuid().ToString(), "admin_banco", $"institucion:{campania.InstitucionId}");
            var r = await new CambiarEstadoCampania(c, new RegistradorAuditoriaCamp(c), new InstitucionesProyectadas(c))
                .EjecutarAsync(admin, campania.Id, Accion.Publicar, Guid.NewGuid().ToString());
            Assert.True(r.Exitoso);
        }
        await using var verificacion = _base.Contexto();
        var evento = await verificacion.EventosSalida.SingleAsync(e => e.Clave == campania.Id.ToString());
        // El registro de auditoría también salió por la bandeja: el tipo lo admite la base.
        Assert.True(await verificacion.EventosSalida.AnyAsync(e => e.Tipo == "audit.recorded" && e.Tema == RegistradorAuditoriaCamp.Tema));
        Assert.True(evento.Secuencia > 0);
        Assert.Equal("pendiente", evento.Estado);
        Assert.Contains("\"campania_id\"", evento.Carga);
    }

    [Fact]
    public async Task Una_donacion_se_cuenta_una_sola_vez_aunque_el_evento_se_repita()
    {
        var campania = await CrearAsync("publicada");
        var eventoId = Guid.NewGuid();
        var evento = new DonacionCompletadaV1(Guid.NewGuid(), campania.Id);

        await using (var c = _base.Contexto())
        {
            Assert.True(await new AplicarDonacionCompletada(c).EjecutarAsync(eventoId, evento));
        }
        await using (var c = _base.Contexto())
        {
            Assert.False(await new AplicarDonacionCompletada(c).EjecutarAsync(eventoId, evento));
            Assert.True(await new AplicarDonacionCompletada(c).EjecutarAsync(Guid.NewGuid(), evento with { DonacionId = Guid.NewGuid() }));
            Assert.True(await new AplicarDonacionCompletada(c).EjecutarAsync(Guid.NewGuid(), new DonacionCompletadaV1(Guid.NewGuid(), null)));
        }
        await using var verificacion = _base.Contexto();
        Assert.Equal(2, (await verificacion.Campanias.SingleAsync(x => x.Id == campania.Id)).DonacionesRegistradas);
    }

    private static InstitucionCambiadaV1 Institucion(Guid id, string nombre, DateTimeOffset cambiadoEn, bool vigente = true) =>
        new(id, "QA-" + id.ToString()[..6], nombre, "banco_de_sangre", "05001", "/00/05/05001", vigente, cambiadoEn);

    [Fact]
    public async Task La_institucion_se_guarda_una_vez_y_un_evento_viejo_no_pisa_uno_nuevo()
    {
        var id = Guid.NewGuid();
        var ahora = DateTimeOffset.UtcNow;
        var eventoId = Guid.NewGuid();

        await using (var c = _base.Contexto())
        {
            Assert.True(await new AplicarInstitucionCambiada(c).EjecutarAsync(eventoId, Institucion(id, "Banco Uno", ahora)));
            Assert.False(await new AplicarInstitucionCambiada(c).EjecutarAsync(eventoId, Institucion(id, "Banco Uno", ahora)));
            Assert.True(await new AplicarInstitucionCambiada(c).EjecutarAsync(Guid.NewGuid(), Institucion(id, "Banco Renombrado", ahora.AddMinutes(1))));
            // Llega tarde un evento anterior: se registra como procesado, pero no cambia nada.
            Assert.True(await new AplicarInstitucionCambiada(c).EjecutarAsync(Guid.NewGuid(), Institucion(id, "Nombre Viejo", ahora.AddMinutes(-5), vigente: false)));
        }
        await using var verificacion = _base.Contexto();
        var guardada = await verificacion.Instituciones.SingleAsync(i => i.InstitucionId == id);
        Assert.Equal(("Banco Renombrado", true), (guardada.Nombre, guardada.Vigente));
        var resuelta = await new InstitucionesProyectadas(verificacion).ResolverAsync(id);
        Assert.Equal("/00/05/05001", resuelta!.TerritorioRuta);
    }

    [Fact]
    public async Task La_depuracion_borra_solo_lo_publicado_o_procesado_que_ya_vencio()
    {
        var vencido = Guid.NewGuid();
        var vigente = Guid.NewGuid();
        var pendiente = Guid.NewGuid();
        var procesadoVencido = Guid.NewGuid();
        var procesadoVigente = Guid.NewGuid();
        await using (var conexion = new NpgsqlConnection(_base.CadenaServicio))
        {
            await conexion.OpenAsync();
            var sql = $$"""
                INSERT INTO evento_salida (id, tipo, version_esquema, tema, clave, carga, correlacion_id, estado, publicado_en, expira_en)
                VALUES ('{{vencido}}', 'audit.recorded', 1, 't', 'c', '{}', 'c', 'publicado', now() - interval '10 days', now() - interval '1 day'),
                       ('{{vigente}}', 'audit.recorded', 1, 't', 'c', '{}', 'c', 'publicado', now(), now() + interval '6 days'),
                       ('{{pendiente}}', 'audit.recorded', 1, 't', 'c', '{}', 'c', 'pendiente', NULL, NULL);
                INSERT INTO evento_procesado (grupo_consumidor, evento_id, tipo, procesado_en, expira_en)
                VALUES ('g', '{{procesadoVencido}}', 't', now() - interval '20 days', now() - interval '1 day'),
                       ('g', '{{procesadoVigente}}', 't', now(), now() + interval '13 days');
                """;
            await new NpgsqlCommand(sql, conexion).ExecuteNonQueryAsync();
        }

        await using (var c = _base.Contexto())
        {
            Assert.True(await DepuradorRegistrosTecnicos.DepurarAsync(c, CancellationToken.None) >= 2);
        }

        await using var verificacion = new NpgsqlConnection(_base.CadenaServicio);
        await verificacion.OpenAsync();
        async Task<bool> Existe(string tabla, string columna, Guid id) =>
            (long)(await new NpgsqlCommand($"SELECT count(*) FROM {tabla} WHERE {columna} = '{id}'", verificacion).ExecuteScalarAsync())! == 1;
        Assert.False(await Existe("evento_salida", "id", vencido));
        Assert.True(await Existe("evento_salida", "id", vigente));
        Assert.True(await Existe("evento_salida", "id", pendiente));
        Assert.False(await Existe("evento_procesado", "evento_id", procesadoVencido));
        Assert.True(await Existe("evento_procesado", "evento_id", procesadoVigente));

        // Nada de esta prueba queda pendiente: un publicador de otra prueba lo intentaría enviar.
        await new NpgsqlCommand($"DELETE FROM evento_salida WHERE id IN ('{vigente}', '{pendiente}')", verificacion).ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task El_servicio_no_puede_borrar_campanias_ni_reescribir_el_evento()
    {
        await using var conexion = new NpgsqlConnection(_base.CadenaServicio);
        await conexion.OpenAsync();
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await new NpgsqlCommand("DELETE FROM campania", conexion).ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await new NpgsqlCommand("UPDATE evento_salida SET carga = '{}'::jsonb", conexion).ExecuteNonQueryAsync());
    }
}
