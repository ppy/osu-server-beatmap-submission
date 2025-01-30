// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using osu.Server.BeatmapSubmission.Authentication;

namespace osu.Server.BeatmapSubmission.Tests
{
    /// <seealso href="https://github.com/dotnet/AspNetCore.Docs.Samples/blob/main/test/integration-tests/8.x/IntegrationTestsSample/tests/RazorPagesProject.Tests/CustomWebApplicationFactory.cs"/>
    public class IntegrationTestWebApplicationFactory<TProgram> : WebApplicationFactory<TProgram>
        where TProgram : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // this is a non-standard environment string (usually it's one of "Development", "Staging", or "Production").
            // this is primarily done such that integration tests that use this factory have full control over dependency injection.
            builder.UseEnvironment(Program.INTEGRATION_TEST_ENVIRONMENT);
            builder.ConfigureTestServices(services =>
            {
                // use the `HeaderBasedAuthenticationHandler` so that users can be easily impersonated for testing needs.
                services.AddAuthentication(config =>
                {
                    config.DefaultAuthenticateScheme = HeaderBasedAuthenticationHandler.AUTH_SCHEME;
                    config.DefaultChallengeScheme = HeaderBasedAuthenticationHandler.AUTH_SCHEME;
                }).AddScheme<AuthenticationSchemeOptions, HeaderBasedAuthenticationHandler>(HeaderBasedAuthenticationHandler.AUTH_SCHEME, null);
            });
        }
    }
}
