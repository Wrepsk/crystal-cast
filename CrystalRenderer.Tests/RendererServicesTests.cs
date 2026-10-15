using System.Reflection;
using CrystalRenderer.Integration;

namespace CrystalRenderer.Tests;

public sealed class RendererServicesTests
{
    [Fact]
    public void RequiredServicesAreConstructorInjectedAndImmutable()
    {
        var constructor = Assert.Single(typeof(RendererServices).GetConstructors());
        var properties = typeof(RendererServices).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.Equal(5, constructor.GetParameters().Length);
        Assert.Equal(5, properties.Length);
        Assert.All(properties, property =>
        {
            Assert.Null(property.SetMethod);
            Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == property.PropertyType);
        });
    }

    [Fact]
    public void MissingServicesAreRejectedBeforeGraphicsInitialization()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new RendererServices(null!, null!, null!, null!, null!));
        Assert.Equal("gameGui", exception.ParamName);
    }
}
