// RedVital — campaign-service
// T-312.1 (mapeo) y T-312.5 (serie de auditoría). Mapea contra
// databases/campanas/schema.sql; nunca llama a Migrate() ni EnsureCreated().

using Microsoft.EntityFrameworkCore;
using RedVital.Campanas.Dominio;

namespace RedVital.Campanas.Infraestructura.Persistencia;

public class CampanasDbContext : DbContext
{
    public CampanasDbContext(DbContextOptions<CampanasDbContext> options) : base(options) { }

    public DbSet<Campania> Campanias => Set<Campania>();
    public DbSet<ReservaCupo> ReservasCupo => Set<ReservaCupo>();
    public DbSet<RegistroAuditoriaCamp> RegistroAuditoriaCamp => Set<RegistroAuditoriaCamp>();
    public DbSet<EventoSalida> EventosSalida => Set<EventoSalida>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Campania>(e =>
        {
            e.ToTable("campania");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id");
            e.Ignore(c => c.CupoDisponible); // calculado, no es columna
            e.Property(c => c.InstitucionId).HasColumnName("institucion_id");
            e.Property(c => c.TerritorioCodigo).HasColumnName("territorio_codigo");
            e.Property(c => c.TerritorioRuta).HasColumnName("territorio_ruta");
            e.Property(c => c.Nombre).HasColumnName("nombre");
            e.Property(c => c.Descripcion).HasColumnName("descripcion");
            e.Property(c => c.Sede).HasColumnName("sede");
            e.Property(c => c.IniciaEn).HasColumnName("inicia_en");
            e.Property(c => c.TerminaEn).HasColumnName("termina_en");
            e.Property(c => c.CupoTotal).HasColumnName("cupo_total");
            e.Property(c => c.CupoReservado).HasColumnName("cupo_reservado");
            e.Property(c => c.Estado).HasColumnName("estado");
            e.Property(c => c.PublicadaEn).HasColumnName("publicada_en");
            e.Property(c => c.CreadaPor).HasColumnName("creada_por");
            e.Property(c => c.CreadaEn).HasColumnName("creada_en");
            e.Property(c => c.ActualizadaEn).HasColumnName("actualizada_en");
            e.Property(c => c.DonacionesRegistradas).HasColumnName("donaciones_registradas");
        });

        m.Entity<EventoSalida>(e =>
        {
            e.ToTable("evento_salida");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            // La genera la base (identidad): fija el orden de publicación y nunca sale del servicio.
            e.Property(x => x.Secuencia).HasColumnName("secuencia").ValueGeneratedOnAdd();
            e.Property(x => x.Tipo).HasColumnName("tipo");
            e.Property(x => x.VersionEsquema).HasColumnName("version_esquema");
            e.Property(x => x.Tema).HasColumnName("tema");
            e.Property(x => x.Clave).HasColumnName("clave");
            e.Property(x => x.Carga).HasColumnName("carga").HasColumnType("jsonb");
            e.Property(x => x.ClaveNatural).HasColumnName("clave_natural");
            e.Property(x => x.CorrelacionId).HasColumnName("correlacion_id");
            e.Property(x => x.Estado).HasColumnName("estado");
            e.Property(x => x.IntentosPublicacion).HasColumnName("intentos_publicacion");
            e.Property(x => x.CreadoEn).HasColumnName("creado_en");
            e.Property(x => x.PublicadoEn).HasColumnName("publicado_en");
            e.Property(x => x.ExpiraEn).HasColumnName("expira_en");
        });

        m.Entity<ReservaCupo>(e =>
        {
            e.ToTable("reserva_cupo");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.CampaniaId).HasColumnName("campania_id");
            e.Property(r => r.UsuarioId).HasColumnName("usuario_id");
            e.Property(r => r.Estado).HasColumnName("estado");
            e.Property(r => r.CreadaEn).HasColumnName("creada_en");
            e.Property(r => r.ExpiraEn).HasColumnName("expira_en");
            e.Property(r => r.ConfirmadaEn).HasColumnName("confirmada_en");
            e.Property(r => r.CerradaEn).HasColumnName("cerrada_en");
        });

        m.Entity<RegistroAuditoriaCamp>(e =>
        {
            e.ToTable("registro_auditoria_camp");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.ActorTipo).HasColumnName("actor_tipo");
            e.Property(r => r.ActorId).HasColumnName("actor_id");
            e.Property(r => r.Rol).HasColumnName("rol");
            e.Property(r => r.JurisdiccionSolicitada).HasColumnName("jurisdiccion_solicitada");
            e.Property(r => r.Operacion).HasColumnName("operacion");
            e.Property(r => r.RecursoTipo).HasColumnName("recurso_tipo");
            e.Property(r => r.RecursoId).HasColumnName("recurso_id");
            e.Property(r => r.Resultado).HasColumnName("resultado");
            e.Property(r => r.CorrelacionId).HasColumnName("correlacion_id");
            e.Property(r => r.Origen).HasColumnName("origen");
            e.Property(r => r.OcurridoEn).HasColumnName("ocurrido_en");
        });
    }
}

/// <summary>T-312.5 — solo inserta. No existe método de actualización ni borrado.</summary>
public class RegistradorAuditoriaCamp : IRegistradorAuditoriaCamp
{
    private readonly CampanasDbContext _contexto;
    public RegistradorAuditoriaCamp(CampanasDbContext contexto) => _contexto = contexto;

    public async Task RegistrarAsync(RegistroAuditoriaCamp registro, CancellationToken ct = default)
    {
        _contexto.RegistroAuditoriaCamp.Add(registro);
        await _contexto.SaveChangesAsync(ct);
    }
}
