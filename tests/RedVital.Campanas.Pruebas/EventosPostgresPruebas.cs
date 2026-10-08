// Pruebas de integración de los eventos de Campañas contra PostgreSQL real,
// con los roles y las migraciones del repositorio de bases. El servicio se
// conecta como campana_servicio, así que también prueban los permisos.

using Microsoft.EntityFrameworkCore;
using Npgsql;
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
            var r = await new CambiarEstadoCampania(c, new RegistradorAuditoriaCamp(c))
                .EjecutarAsync(admin, campania.Id, Accion.Publicar, Guid.NewGuid().ToString());
            Assert.True(r.Exitoso);
        }
        await using var verificacion = _base.Contexto();
        var evento = await verificacion.EventosSalida.SingleAsync(e => e.Clave == campania.Id.ToString());
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
