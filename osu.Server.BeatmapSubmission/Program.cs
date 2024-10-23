using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using osu.Server.BeatmapSubmission.Authentication;

namespace osu.Server.BeatmapSubmission
{
    [UsedImplicitly]
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddAuthorization();
            builder.Services.AddControllers();
            builder.Services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                // TODO: sentry
            });

            if (builder.Environment.IsDevelopment())
            {
                // constrain docs tools to development instances for now.
                // I don't really think we want them out in public.
                // TODO: confirm this
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen();
                builder.Services.AddAuthentication(config =>
                {
                    config.DefaultAuthenticateScheme = LocalAuthenticationHandler.AUTH_SCHEME;
                    config.DefaultChallengeScheme = LocalAuthenticationHandler.AUTH_SCHEME;
                }).AddScheme<AuthenticationSchemeOptions, LocalAuthenticationHandler>(LocalAuthenticationHandler.AUTH_SCHEME, null);
            }
            else
            {
                builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, OsuWebSharedJwtBearerOptions>();
                builder.Services.AddAuthentication(config =>
                       {
                           config.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                           config.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                       })
                       .AddJwtBearer();
            }

            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseAuthorization();
            app.MapControllers();

            app.Run();
        }
    }
}