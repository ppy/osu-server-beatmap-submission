using System.Reflection;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using osu.Server.BeatmapSubmission.Authentication;
using osu.Server.BeatmapSubmission.Configuration;
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
            builder.Services.AddControllers(options =>
            {
                options.Filters.Add<InvariantExceptionFilter>();
            });
            builder.Services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                if (AppSettings.SentryDsn != null)
                    logging.AddSentry();
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
                    builder.Services.AddHttpClient();
                    builder.Services.AddTransient<ILegacyIO, LegacyIO>();
                    builder.Services.AddTransient<IMirrorService, NoOpMirrorService>();
                    builder.Services.AddSwaggerGen(c =>
                    {
                        c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, Assembly.GetExecutingAssembly().GetName().Name + ".xml"));
                    });
                    break;
                }

                case "Staging":
                case "Production":
                {
                    builder.Services.AddSingleton<IBeatmapStorage, S3BeatmapStorage>();
                    builder.Services.AddTransient<BeatmapPackagePatcher>();
                    builder.Services.AddHttpClient();
                    builder.Services.AddTransient<ILegacyIO, LegacyIO>();
                    builder.Services.AddTransient<IMirrorService, MirrorService>();

                    if (AppSettings.SentryDsn == null)
                    {
                        throw new InvalidOperationException("SENTRY_DSN environment variable not set. "
                                                            + "Please set the value of this variable to a valid Sentry DSN to use for logging events.");
                    }

                    break;
                }
            }

            if (AppSettings.SentryDsn != null)
                builder.WebHost.UseSentry(options => options.Dsn = AppSettings.SentryDsn);

            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseReDoc();
            }

            app.UseAuthorization();
            app.MapControllers();

            app.Run();
        }
    }
}