// RedVital — campaign-service
// POST /v1/campanias       Crear campaña (U4): nace en borrador, con la
//                          institución del token y el municipio que tiene
//                          la copia local de instituciones (EV-07).
// PUT  /v1/campanias/{id}  Editar campaña (U4): solo en borrador.
// Toda operación, permitida o denegada, queda en registro_auditoria_camp.

using Microsoft.EntityFrameworkCore;
using RedVital.Campanas.Dominio;
using RedVital.Campanas.Infraestructura.Persistencia;

namespace RedVital.Campanas.Aplicacion.CasosDeUso;

public sealed record DatosCampania(string? Nombre, string? Descripcion, string? Sede, DateTimeOffset? IniciaEn,
    DateTimeOffset? TerminaEn, int? CupoTotal);

/// <summary>Reglas del contenido de una campaña, las mismas al crear y al editar (DD Sección 8).</summary>
public static class ReglasCampania
{
    public static string? Validar(Campania c, DateTimeOffset ahora)
    {
        if (string.IsNullOrWhiteSpace(c.Nombre) || c.Nombre.Length > 160) return "nombre es obligatorio y admite hasta 160 caracteres.";
        if (string.IsNullOrWhiteSpace(c.Sede) || c.Sede.Length > 200) return "sede es obligatoria y admite hasta 200 caracteres.";
        if (c.Descripcion is { Length: > 500 }) return "descripcion admite hasta 500 caracteres.";
        if (c.TerminaEn <= c.IniciaEn) return "termina_en debe ser posterior a inicia_en.";
        if (c.TerminaEn <= ahora) return "La campaña no puede terminar en el pasado.";
        if (c.CupoTotal is <= 0) return "cupo_total debe ser positivo, o nulo para no limitarlo.";
        return null;
    }

    public static string? Limpio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}

public class CrearCampania
{
    private readonly CampanasDbContext _contexto;
    private readonly IRegistradorAuditoriaCamp _auditoria;
    private readonly IResolutorInstitucion _instituciones;

    public CrearCampania(CampanasDbContext contexto, IRegistradorAuditoriaCamp auditoria, IResolutorInstitucion instituciones)
    {
        _contexto = contexto;
        _auditoria = auditoria;
        _instituciones = instituciones;
    }

    public async Task<Resultado<CampaniaRespuesta>> EjecutarAsync(Solicitante s, DatosCampania d,
        string correlacionId, CancellationToken ct = default)
    {
        if (!s.EsAdminBanco || s.InstitucionJurisdiccion is not { } institucion || !Guid.TryParse(s.Sub, out var autor))
        {
            await _auditoria.RegistrarAsync(Auditar.Hecho(s, "crear_campania", null, "denegado", correlacionId), ct);
            return Resultado<CampaniaRespuesta>.Error(Fallo.NoPermitido);
        }
        if (d.IniciaEn is not { } inicia || d.TerminaEn is not { } termina)
            return Resultado<CampaniaRespuesta>.Error(Fallo.Invalido, "inicia_en y termina_en son obligatorias.");

        // Sin el evento de la institución no se sabe su municipio, y una campaña
        // sin ruta no sería filtrable por jurisdicción (DD 13.2).
        var propia = await _instituciones.ResolverAsync(institucion, ct);
        if (propia is null)
            return Resultado<CampaniaRespuesta>.Error(Fallo.NoDisponible,
                "No es posible crear la campaña en este momento: aún no se conoce la ubicación de su institución. Inténtalo en unos minutos.");
        if (!propia.Vigente)
            return Resultado<CampaniaRespuesta>.Error(Fallo.Conflicto, "Su institución no está vigente en el registro institucional.");

        var ahora = DateTimeOffset.UtcNow;
        var c = new Campania
        {
            Id = Guid.NewGuid(),
            InstitucionId = institucion,
            TerritorioCodigo = propia.TerritorioCodigo,
            TerritorioRuta = propia.TerritorioRuta,
            Nombre = ReglasCampania.Limpio(d.Nombre) ?? "",
            Descripcion = ReglasCampania.Limpio(d.Descripcion),
            Sede = ReglasCampania.Limpio(d.Sede) ?? "",
            IniciaEn = inicia.ToUniversalTime(),
            TerminaEn = termina.ToUniversalTime(),
            CupoTotal = d.CupoTotal,
            Estado = "borrador",
            CreadaPor = autor,
            CreadaEn = ahora,
            ActualizadaEn = ahora,
        };
        if (ReglasCampania.Validar(c, ahora) is { } error) return Resultado<CampaniaRespuesta>.Error(Fallo.Invalido, error);

        _contexto.Campanias.Add(c);
        await _contexto.SaveChangesAsync(ct);
        await _auditoria.RegistrarAsync(Auditar.Hecho(s, "crear_campania", c.Id, "permitido", correlacionId), ct);
        return Resultado<CampaniaRespuesta>.Ok(CampaniaRespuesta.Desde(c, propia.Nombre, institucional: true));
    }
}

public class EditarCampania
{
    private readonly CampanasDbContext _contexto;
    private readonly IRegistradorAuditoriaCamp _auditoria;
    private readonly IResolutorInstitucion _instituciones;

    public EditarCampania(CampanasDbContext contexto, IRegistradorAuditoriaCamp auditoria, IResolutorInstitucion instituciones)
    {
        _contexto = contexto;
        _auditoria = auditoria;
        _instituciones = instituciones;
    }

    public async Task<Resultado<CampaniaRespuesta>> EjecutarAsync(Solicitante s, Guid id, DatosCampania d, string correlacionId,
        CancellationToken ct = default)
    {
        if (!s.EsAdminBanco)
        {
            await _auditoria.RegistrarAsync(Auditar.Hecho(s, "editar_campania", id, "denegado", correlacionId), ct);
            return Resultado<CampaniaRespuesta>.Error(Fallo.NoPermitido);
        }
        var c = await _contexto.Campanias.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Resultado<CampaniaRespuesta>.Error(Fallo.NoEncontrado);
        if (!s.Alcanza(c))
        {
            await _auditoria.RegistrarAsync(Auditar.Hecho(s, "editar_campania", id, "denegado", correlacionId,
                jurisdiccionSolicitada: $"institucion:{c.InstitucionId}"), ct);
            return Resultado<CampaniaRespuesta>.Error(Fallo.NoEncontrado);
        }
        if (c.Estado != "borrador") return Resultado<CampaniaRespuesta>.Error(Fallo.Conflicto, "Solo se edita una campaña en borrador.");

        if (d.Nombre is not null) c.Nombre = d.Nombre.Trim();
        if (d.Descripcion is not null) c.Descripcion = ReglasCampania.Limpio(d.Descripcion);
        if (d.Sede is not null) c.Sede = d.Sede.Trim();
        if (d.IniciaEn is { } inicia) c.IniciaEn = inicia.ToUniversalTime();
        if (d.TerminaEn is { } termina) c.TerminaEn = termina.ToUniversalTime();
        if (d.CupoTotal is not null) c.CupoTotal = d.CupoTotal;
        var ahora = DateTimeOffset.UtcNow;
        if (ReglasCampania.Validar(c, ahora) is { } error) return Resultado<CampaniaRespuesta>.Error(Fallo.Invalido, error);

        c.ActualizadaEn = ahora;
        await _contexto.SaveChangesAsync(ct);
        await _auditoria.RegistrarAsync(Auditar.Hecho(s, "editar_campania", id, "permitido", correlacionId), ct);
        return Resultado<CampaniaRespuesta>.Ok(CampaniaRespuesta.Desde(c,
            await NombresInstitucion.DeAsync(_instituciones, c.InstitucionId, ct), institucional: true));
    }
}
