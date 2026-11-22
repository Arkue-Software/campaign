// Qué cuenta como «la base no responde» para el consumidor de Campañas: eso
// se espera y se reintenta; lo demás es un fallo del mensaje.

using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RedVital.Campanas.Infraestructura.Persistencia;
using Xunit;

namespace RedVital.Campanas.Pruebas;

public class FallosTransitoriosPruebas
{
    [Fact]
    public void La_conexion_caida_es_de_la_base_aunque_EF_la_envuelva()
    {
        var caida = new NpgsqlException("Failed to connect", new SocketException((int)SocketError.ConnectionRefused));
        Assert.True(FallosTransitorios.EsDeLaBase(caida));
        Assert.True(FallosTransitorios.EsDeLaBase(new DbUpdateException("No se guardó", caida)));
        Assert.True(FallosTransitorios.EsDeLaBase(new TimeoutException()));
    }

    [Theory]
    [InlineData("57P03")] // la base está arrancando
    [InlineData("53300")] // demasiadas conexiones
    [InlineData("40001")] // conflicto de serialización
    public void Los_estados_transitorios_de_PostgreSQL_son_de_la_base(string estado) =>
        Assert.True(FallosTransitorios.EsDeLaBase(new PostgresException("transitorio", "FATAL", "FATAL", estado)));

    [Fact]
    public void Un_error_del_mensaje_o_de_permisos_no_es_de_la_base()
    {
        Assert.False(FallosTransitorios.EsDeLaBase(new InvalidOperationException("dato inválido")));
        Assert.False(FallosTransitorios.EsDeLaBase(new PostgresException("sin permiso", "ERROR", "ERROR", "42501")));
        Assert.False(FallosTransitorios.EsDeLaBase(new DbUpdateException("violación", new PostgresException("único", "ERROR", "ERROR", "23505"))));
    }
}
