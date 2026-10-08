// T-312.6 — Pruebas del servicio de Campañas con tokens de prueba.
// Cubre T-312.2, T-312.3, T-312.4 y T-312.5.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RedVital.Campanas.Aplicacion.CasosDeUso;
using RedVital.Campanas.Dominio;
using RedVital.Campanas.Infraestructura.Persistencia;
using Xunit;

namespace RedVital.Campanas.Pruebas;

public class CampaniasPruebas
{
    // ---------- tokens de prueba: se firman y se VALIDAN, como en el servicio ----------
    private static readonly RSA LlaveDePrueba = RSA.Create(2048);
    private static readonly Guid InstA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InstB = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static Solicitante Token(string sub, string role, string jurisdiction)
    {
        var ahora = DateTimeOffset.UtcNow;
        var jwt = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: new[]
        {
            new Claim("sub", sub), new Claim("role", role), new Claim("jurisdiction", jurisdiction),
            new Claim("iss", "https://identidad.redvital.local"), new Claim("aud", "redvital"),
            new Claim("iat", ahora.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim("exp", ahora.AddMinutes(15).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim("jti", Guid.NewGuid().ToString()),
        }, signingCredentials: new SigningCredentials(new RsaSecurityKey(LlaveDePrueba) { KeyId = "dev-key-1" }, SecurityAlgorithms.RsaSha256)));

        var p = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(jwt, new TokenValidationParameters
        {
            ValidIssuer = "https://identidad.redvital.local",
            ValidAudience = "redvital",
            IssuerSigningKey = new RsaSecurityKey(LlaveDePrueba),
        }, out _);
        return new Solicitante(p.FindFirst("sub")!.Value, p.FindFirst("role")!.Value, p.FindFirst("jurisdiction")!.Value);
    }

    private static Solicitante Operador(Guid inst) => Token(Guid.NewGuid().ToString(), "operador", $"institucion:{inst}");
    private static Solicitante AdminBanco(Guid inst) => Token(Guid.NewGuid().ToString(), "admin_banco", $"institucion:{inst}");
    private static Solicitante Coordinador(string ruta) => Token(Guid.NewGuid().ToString(), "coordinador", $"territorio:{ruta}");
    private static Solicitante Donante() => Token(Guid.NewGuid().ToString(), "donante", "");
    private static Solicitante Auditor() => Token(Guid.NewGuid().ToString(), "auditor", "territorio:/00");
    private static Solicitante ServicioDonacion() => Token("donacion", "servicio", "");

    // ---------- datos sintéticos ----------
    private static CampanasDbContext Contexto()
    {
        var c = new CampanasDbContext(new DbContextOptionsBuilder<CampanasDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        c.Campanias.AddRange(
            Nueva("A-publicada", InstA, "/00/11/11001", "publicada", cupoTotal: 50, reservado: 12),
            Nueva("A-borrador", InstA, "/00/11/11001", "borrador"),
            Nueva("B-publicada", InstB, "/00/25/25175", "publicada"));
        c.SaveChanges();
        return c;
    }

    private static Campania Nueva(string nombre, Guid inst, string ruta, string estado, int? cupoTotal = null, int reservado = 0) => new()
    {
        Id = Guid.NewGuid(), InstitucionId = inst, TerritorioCodigo = ruta[^5..], TerritorioRuta = ruta,
        Nombre = nombre, Sede = "Sede ficticia", IniciaEn = DateTimeOffset.UtcNow.AddDays(1),
        TerminaEn = DateTimeOffset.UtcNow.AddDays(3), CupoTotal = cupoTotal, CupoReservado = reservado,
        Estado = estado, CreadaPor = Guid.NewGuid(), CreadaEn = DateTimeOffset.UtcNow, ActualizadaEn = DateTimeOffset.UtcNow,
    };

    private static Guid IdDe(CampanasDbContext c, string nombre) => c.Campanias.Single(x => x.Nombre == nombre).Id;
    private static ConsultarCampanias Consultar(CampanasDbContext c) => new(c, new RegistradorAuditoriaCamp(c));
    private static ConsultarCampania Detalle(CampanasDbContext c) => new(c, new RegistradorAuditoriaCamp(c));
    private static CambiarEstadoCampania Cambiar(CampanasDbContext c) => new(c, new RegistradorAuditoriaCamp(c));
    private static readonly ConsultaCampanias SinFiltro = new(null, null, null);

    // ---------- T-312.2 ----------
    [Fact]
    public async Task Anonimo_y_donante_solo_ven_campanias_publicadas()
    {
        var c = Contexto();
        var anonimo = await Consultar(c).EjecutarAsync(Solicitante.Anonimo, SinFiltro, "corr");
        var donante = await Consultar(c).EjecutarAsync(Donante(), SinFiltro, "corr");

        Assert.All(anonimo.Valor!, x => Assert.Equal("publicada", x.Estado));
        Assert.Equal(2, anonimo.Valor!.Count);
        Assert.Equal(2, donante.Valor!.Count);
    }

    [Fact]
    public async Task Anonimo_puede_filtrar_por_territorio()
    {
        var c = Contexto();
        var r = await Consultar(c).EjecutarAsync(Solicitante.Anonimo, new(null, null, "/00/25"), "corr");
        Assert.Equal("B-publicada", Assert.Single(r.Valor!).Nombre);
    }

    [Fact]
    public async Task Operador_ve_solo_su_institucion_y_sin_borradores()
    {
        var c = Contexto();
        var r = await Consultar(c).EjecutarAsync(Operador(InstA), SinFiltro, "corr");
        Assert.Equal("A-publicada", Assert.Single(r.Valor!).Nombre);
    }

    [Fact]
    public async Task Administrador_de_banco_ve_los_borradores_de_su_institucion()
    {
        var c = Contexto();
        var r = await Consultar(c).EjecutarAsync(AdminBanco(InstA), SinFiltro, "corr");
        Assert.Equal(new[] { "A-borrador", "A-publicada" }, r.Valor!.Select(x => x.Nombre).OrderBy(x => x));
    }

    [Theory]
    [InlineData("/00/11", "A-publicada")]   // departamento cubre a su municipio
    [InlineData("/00/25", "B-publicada")]
    public async Task Coordinador_ve_solo_su_territorio(string ruta, string esperada)
    {
        var c = Contexto();
        var r = await Consultar(c).EjecutarAsync(Coordinador(ruta), SinFiltro, "corr");
        Assert.Equal(esperada, Assert.Single(r.Valor!).Nombre);
    }

    [Fact]
    public async Task Ruta_parecida_no_es_ruta_descendiente()
    {
        var c = Contexto();
        var r = await Consultar(c).EjecutarAsync(Coordinador("/00/1"), SinFiltro, "corr"); // no debe alcanzar /00/11
        Assert.Empty(r.Valor!);
    }

    [Fact] // T-312.2 + T-312.5
    public async Task Auditor_es_denegado_y_queda_auditado()
    {
        var c = Contexto();
        var r = await Consultar(c).EjecutarAsync(Auditor(), SinFiltro, "corr-aud");

        Assert.Equal(Fallo.NoPermitido, r.Fallo);
        var reg = Assert.Single(c.RegistroAuditoriaCamp);
        Assert.Equal("denegado", reg.Resultado);
        Assert.Equal("corr-aud", reg.CorrelacionId);
    }

    [Fact] // la respuesta expone cupo_disponible y nunca cupo_reservado
    public async Task Respuesta_expone_cupo_disponible_y_oculta_cupo_reservado()
    {
        var c = Contexto();
        var r = await Consultar(c).EjecutarAsync(Solicitante.Anonimo, new(null, null, "/00/11"), "corr");

        Assert.Equal(38, Assert.Single(r.Valor!).CupoDisponible);
        Assert.DoesNotContain(typeof(CampaniaRespuesta).GetProperties(), p => p.Name.Contains("Reservado"));
    }

    // ---------- T-312.3 ----------
    [Fact]
    public async Task Donante_no_ve_el_detalle_de_un_borrador()
    {
        var c = Contexto();
        var r = await Detalle(c).EjecutarAsync(Donante(), IdDe(c, "A-borrador"), "corr");
        Assert.Equal(Fallo.NoEncontrado, r.Fallo);
    }

    [Fact]
    public async Task Donacion_no_consulta_campanias_por_HTTP_porque_todo_va_por_Kafka()
    {
        var c = Contexto();
        var detalle = await Detalle(c).EjecutarAsync(ServicioDonacion(), IdDe(c, "B-publicada"), "corr");
        var listado = await Consultar(c).EjecutarAsync(ServicioDonacion(), SinFiltro, "corr");

        Assert.Equal(Fallo.NoPermitido, detalle.Fallo);
        Assert.Equal(Fallo.NoPermitido, listado.Fallo);
        Assert.Equal(2, c.RegistroAuditoriaCamp.Count(r => r.Resultado == "denegado"));
    }

    [Fact]
    public async Task Publicar_escribe_el_evento_en_la_bandeja_en_la_misma_operacion()
    {
        var c = Contexto();
        var id = IdDe(c, "A-borrador");
        var r = await Cambiar(c).EjecutarAsync(AdminBanco(InstA), id, Accion.Publicar, "corr-pub");

        Assert.True(r.Exitoso);
        var evento = Assert.Single(c.EventosSalida);
        Assert.Equal("campaign.published", evento.Tipo);
        Assert.Equal("redvital.campanias.campaign-published.v1", evento.Tema);
        Assert.Equal(id.ToString(), evento.Clave);
        Assert.Equal($"campania_publicada:{id}", evento.ClaveNatural);
        Assert.Equal("pendiente", evento.Estado);
        Assert.Equal("corr-pub", evento.CorrelacionId);
        using var carga = System.Text.Json.JsonDocument.Parse(evento.Carga);
        Assert.Equal(id, carga.RootElement.GetProperty("campania_id").GetGuid());
        Assert.Equal("/00/11/11001", carga.RootElement.GetProperty("territorio_ruta").GetString());
        Assert.False(carga.RootElement.TryGetProperty("cupo_reservado", out _));

        // Un reenvío de la publicación no genera un segundo evento.
        await Cambiar(c).EjecutarAsync(AdminBanco(InstA), id, Accion.Publicar, "corr-pub-2");
        Assert.Single(c.EventosSalida);
    }

    [Fact]
    public async Task Cerrar_no_escribe_eventos()
    {
        var c = Contexto();
        await Cambiar(c).EjecutarAsync(AdminBanco(InstA), IdDe(c, "A-publicada"), Accion.Cerrar, "corr");
        Assert.Empty(c.EventosSalida);
    }

    [Fact]
    public async Task El_conteo_de_donaciones_solo_lo_ven_los_roles_institucionales()
    {
        var c = Contexto();
        c.Campanias.Single(x => x.Nombre == "A-publicada").DonacionesRegistradas = 7;
        c.SaveChanges();

        var publico = await Consultar(c).EjecutarAsync(Solicitante.Anonimo, SinFiltro, "corr");
        var institucional = await Consultar(c).EjecutarAsync(Operador(InstA), SinFiltro, "corr");

        Assert.All(publico.Valor!, x => Assert.Null(x.DonacionesRegistradas));
        Assert.Equal(7, institucional.Valor!.Single(x => x.Nombre == "A-publicada").DonacionesRegistradas);
    }

    [Fact]
    public async Task Detalle_fuera_de_jurisdiccion_responde_no_encontrado_y_se_audita()
    {
        var c = Contexto();
        var r = await Detalle(c).EjecutarAsync(Operador(InstA), IdDe(c, "B-publicada"), "corr-fuera");

        Assert.Equal(Fallo.NoEncontrado, r.Fallo); // no confirma que exista
        var reg = Assert.Single(c.RegistroAuditoriaCamp);
        Assert.Equal("territorio:/00/25/25175", reg.JurisdiccionSolicitada);
    }

    // ---------- T-312.4 ----------
    [Fact]
    public async Task Administrador_de_banco_publica_y_cierra_su_campania()
    {
        var c = Contexto();
        var id = IdDe(c, "A-borrador");
        var admin = AdminBanco(InstA);

        var publicada = await Cambiar(c).EjecutarAsync(admin, id, Accion.Publicar, "corr-pub");
        var cerrada = await Cambiar(c).EjecutarAsync(admin, id, Accion.Cerrar, "corr-cierre");

        Assert.Equal("publicada", publicada.Valor!.Estado);
        Assert.NotNull(publicada.Valor.PublicadaEn);
        Assert.Equal("cerrada", cerrada.Valor!.Estado);
        Assert.Equal(2, c.RegistroAuditoriaCamp.Count(x => x.Resultado == "permitido"));
    }

    [Fact]
    public async Task Operador_no_puede_publicar()
    {
        var c = Contexto();
        var r = await Cambiar(c).EjecutarAsync(Operador(InstA), IdDe(c, "A-borrador"), Accion.Publicar, "corr");
        Assert.Equal(Fallo.NoPermitido, r.Fallo);
        Assert.Equal("denegado", Assert.Single(c.RegistroAuditoriaCamp).Resultado);
    }

    [Fact]
    public async Task Administrador_de_otra_institucion_no_puede_publicar()
    {
        var c = Contexto();
        var r = await Cambiar(c).EjecutarAsync(AdminBanco(InstB), IdDe(c, "A-borrador"), Accion.Publicar, "corr");
        Assert.Equal(Fallo.NoEncontrado, r.Fallo);
        Assert.Equal("borrador", c.Campanias.Single(x => x.Nombre == "A-borrador").Estado);
    }

    [Fact]
    public async Task Publicar_dos_veces_es_idempotente_y_cerrar_un_borrador_es_conflicto()
    {
        var c = Contexto();
        var admin = AdminBanco(InstA);

        var otra = await Cambiar(c).EjecutarAsync(admin, IdDe(c, "A-publicada"), Accion.Publicar, "corr");
        var conflicto = await Cambiar(c).EjecutarAsync(admin, IdDe(c, "A-borrador"), Accion.Cerrar, "corr");

        Assert.True(otra.Exitoso);
        Assert.Equal(Fallo.Conflicto, conflicto.Fallo);
    }

    [Fact] // los tokens se rechazan si no vienen firmados con la llave esperada
    public void Token_firmado_con_otra_llave_no_valida()
    {
        using var otra = RSA.Create(2048);
        var jwt = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://identidad.redvital.local", audience: "redvital", expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(otra), SecurityAlgorithms.RsaSha256)));

        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler().ValidateToken(jwt, new TokenValidationParameters
        {
            ValidIssuer = "https://identidad.redvital.local",
            ValidAudience = "redvital",
            IssuerSigningKey = new RsaSecurityKey(LlaveDePrueba),
        }, out _));
    }
}
