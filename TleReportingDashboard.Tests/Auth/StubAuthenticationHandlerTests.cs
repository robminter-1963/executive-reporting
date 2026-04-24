using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Encodings.Web;
using TleReportingDashboard.Web.Services;

namespace TleReportingDashboard.Tests.Auth;

public class StubAuthenticationHandlerTests
{
    private readonly StubAuthenticationHandler _handler;

    public StubAuthenticationHandlerTests()
    {
        var options = new TestOptionsMonitor(new AuthenticationSchemeOptions());
        var loggerFactory = NullLoggerFactory.Instance;
        var encoder = UrlEncoder.Default;
        var environment = new Mock<IHostEnvironment>();
        environment.Setup(e => e.EnvironmentName).Returns("Development");

        _handler = new StubAuthenticationHandler(options, loggerFactory, encoder, environment.Object);

        // Initialize the handler with a scheme and HTTP context
        var scheme = new AuthenticationScheme("TestScheme", "TestScheme", typeof(StubAuthenticationHandler));
        var context = new DefaultHttpContext();
        _handler.InitializeAsync(scheme, context).Wait();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ProducesAuthenticatedPrincipal()
    {
        // Act
        var result = await _handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ContainsExpectedNameClaim()
    {
        // Act
        var result = await _handler.AuthenticateAsync();

        // Assert
        result.Principal!.Identity!.Name.Should().Be("Rob Minter");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ContainsExpectedRoleClaims()
    {
        // Act
        var result = await _handler.AuthenticateAsync();

        // Assert
        var roles = result.Principal!.Claims
            .Where(c => c.Type == ClaimTypes.Role)
            .Select(c => c.Value)
            .ToList();

        roles.Should().Contain("Dashboard.User");
        roles.Should().Contain("Dashboard.Admin");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ContainsPreferredUsernameClaim()
    {
        // Act
        var result = await _handler.AuthenticateAsync();

        // Assert
        var username = result.Principal!.Claims
            .FirstOrDefault(c => c.Type == "preferred_username")?.Value;

        username.Should().Be("rob.minter@ralisservices.com");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ContainsOidClaim()
    {
        // Act
        var result = await _handler.AuthenticateAsync();

        // Assert
        var oid = result.Principal!.Claims
            .FirstOrDefault(c => c.Type == "oid")?.Value;

        oid.Should().NotBeNullOrEmpty();
        Guid.TryParse(oid, out _).Should().BeTrue();
    }

    /// <summary>
    /// Helper to expose AuthenticationSchemeOptions as IOptionsMonitor for testing
    /// </summary>
    private class TestOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public TestOptionsMonitor(AuthenticationSchemeOptions currentValue)
        {
            CurrentValue = currentValue;
        }

        public AuthenticationSchemeOptions CurrentValue { get; }

        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
