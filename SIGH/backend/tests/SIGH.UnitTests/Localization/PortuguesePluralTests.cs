using FluentAssertions;
using SIGH.Application.Common;
using Xunit;

namespace SIGH.UnitTests.Localization;

/// <summary>Mensagens que vão para a tela sem "(s)": 1 → singular; 0 e 2 → plural (mesma regra do front).</summary>
public class PortuguesePluralTests
{
    [Theory]
    [InlineData(0, "0 registros novos")]
    [InlineData(1, "1 registro novo")]
    [InlineData(2, "2 registros novos")]
    [InlineData(1248, "1.248 registros novos")]
    public void Count_UsesSingularOnlyForOne(int count, string expected) =>
        PortuguesePlural.Count(count, "registro novo", "registros novos").Should().Be(expected);

    [Theory]
    [InlineData(1, "Coluna obrigatória não encontrada")]
    [InlineData(2, "Colunas obrigatórias não encontradas")]
    public void Of_ReturnsOnlyTheWords(int count, string expected) =>
        PortuguesePlural.Of(count, "Coluna obrigatória não encontrada", "Colunas obrigatórias não encontradas").Should().Be(expected);
}
