using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using osu.Server.BeatmapSubmission.Authentication;
using osu.Server.BeatmapSubmission.Services;

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

            builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, OsuWebSharedJwtBearerOptions>();
            builder.Services.AddAuthentication(config =>
                   {
                       config.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                       config.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                   })
                   .AddJwtBearer();

            switch (builder.Environment.EnvironmentName)
            {
                case "Development":
                {
                    builder.Services.AddTransient<IBeatmapStorage, LocalBeatmapStorage>();
                    builder.Services.AddTransient<BeatmapPackagePatcher>();
                    break;
                }

                case "Staging":
                case "Production":
                {
                    // TODO: S3-based beatmap storage
                    break;
                }
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