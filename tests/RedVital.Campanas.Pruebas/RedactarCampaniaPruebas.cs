// Crear y editar campañas (DD Tabla 33): el municipio de la campaña sale de la
// copia local de instituciones (EV-07). Sin el evento de la institución, la
// creación se bloquea (DD 13.2).

using Microsoft.EntityFrameworkCore;
using RedVital.Campanas.Aplicacion.CasosDeUso;
using RedVital.Campanas.Dominio;
using RedVital.Campanas.Infraestructura.Persistencia;
using Xunit;

namespace RedVital.Campanas.Pruebas;

public class RedactarCampaniaPruebas
{
    private static readonly Guid Aurora = Guid.Parse("a1000000-0000-4000-8000-000000000001");

    private static CampanasDbContext Contexto(bool conAurora = true, bool vigente = true)
    {
        var c = new CampanasDbContext(new DbContextOptionsBuilder<CampanasDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        if (conAurora)
        {
            c.Instituciones.Add(new InstitucionProyeccion
            {
                InstitucionId = Aurora, Codigo = "QA-AURORA", Nombre = "Banco Aurora", Tipo = "banco_de_sangre",
                TerritorioCodigo = "05001", TerritorioRuta = "/00/05/05001", Vigente = vigente,
                CambiadoEn = DateTimeOffset.UtcNow, ActualizadoEn = DateTimeOffset.UtcNow,
            });
            c.SaveChanges();
        }
        return c;
    }

    private static CrearCampania Crear(CampanasDbContext c) => new(c, new RegistradorAuditoriaCamp(c), new InstitucionesProyectadas(c));

    private static Solicitante Admin(Guid inst) => new(Guid.NewGuid().ToString(), "admin_banco", $"institucion:{inst}");

    private static DatosCampania Datos(string nombre = "Jornada nueva") =>
        new(nombre, null, "Parque Berrío, Medellín", DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(3), 40);

    [Fact]
    public async Task Nace_en_borrador_con_el_municipio_de_la_copia_local_y_se_audita()
    {
        var c = Contexto();
        var r = await Crear(c).EjecutarAsync(Admin(Aurora), Datos(), "corr-1");
        Assert.True(r.Exitoso, r.Detalle);
        Assert.Equal(("borrador", "05001", "/00/05/05001", Aurora), (r.Valor!.Estado, r.Valor.TerritorioCodigo, r.Valor.TerritorioRuta, r.Valor.InstitucionId));
        Assert.Equal("Banco Aurora", r.Valor.InstitucionNombre);
        Assert.Contains(c.RegistroAuditoriaCamp, a => a.Operacion == "crear_campania" && a.Resultado == "permitido");
    }

    [Fact]
    public async Task Sin_el_evento_de_la_institucion_no_se_crea_nada_y_es_503()
    {
        var c = Contexto(conAurora: false);
        var r = await Crear(c).EjecutarAsync(Admin(Aurora), Datos(), "corr");
        Assert.Equal(Fallo.NoDisponible, r.Fallo);
        Assert.Empty(c.Campanias);
    }

    [Fact]
    public async Task Solo_el_administrador_de_banco_y_con_datos_validos()
    {
        var c = Contexto();
        var crear = Crear(c);
        var coord = new Solicitante(Guid.NewGuid().ToString(), "coordinador", "territorio:/00/05");
        Assert.Equal(Fallo.NoPermitido, (await crear.EjecutarAsync(coord, Datos(), "c")).Fallo);
        Assert.Contains(c.RegistroAuditoriaCamp, a => a.Operacion == "crear_campania" && a.Resultado == "denegado");
        var alReves = Datos() with { TerminaEn = DateTimeOffset.UtcNow.AddDays(1) };
        Assert.Equal(Fallo.Invalido, (await crear.EjecutarAsync(Admin(Aurora), alReves, "c")).Fallo);
        Assert.Equal(Fallo.Invalido, (await crear.EjecutarAsync(Admin(Aurora), Datos(""), "c")).Fallo);
        Assert.Empty(c.Campanias);
    }

    [Fact]
    public async Task Una_institucion_dada_de_baja_no_crea_campanias()
    {
        var c = Contexto(vigente: false);
        Assert.Equal(Fallo.Conflicto, (await Crear(c).EjecutarAsync(Admin(Aurora), Datos(), "c")).Fallo);
        Assert.Empty(c.Campanias);
    }

    [Fact]
    public async Task Se_edita_solo_en_borrador_y_en_la_propia_institucion()
    {
        var c = Contexto();
        var creada = (await Crear(c).EjecutarAsync(Admin(Aurora), Datos(), "c")).Valor!;
        var editar = new EditarCampania(c, new RegistradorAuditoriaCamp(c), new InstitucionesProyectadas(c));

        var r = await editar.EjecutarAsync(Admin(Aurora), creada.Id, new DatosCampania("Jornada renombrada", null, null, null, null, null), "c");
        Assert.Equal("Jornada renombrada", r.Valor!.Nombre);
        Assert.Equal("Banco Aurora", r.Valor.InstitucionNombre);
        Assert.Equal(Fallo.NoEncontrado, (await editar.EjecutarAsync(Admin(Guid.NewGuid()), creada.Id, Datos(), "c")).Fallo);

        var campania = c.Campanias.Single();
        campania.Estado = "publicada";
        campania.PublicadaEn = DateTimeOffset.UtcNow;
        await c.SaveChangesAsync();
        Assert.Equal(Fallo.Conflicto, (await editar.EjecutarAsync(Admin(Aurora), creada.Id, Datos("Otra"), "c")).Fallo);
    }
}
