using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Options;
using osu.Server.BeatmapSubmission.Authentication;
using osu.Server.BeatmapSubmission.Configuration;
using osu.Server.BeatmapSubmission.Logging;
using osu.Server.BeatmapSubmission.Services;
using StatsdClient;

namespace osu.Server.BeatmapSubmission
{
    [UsedImplicitly]
    public class Program
    {
        public const string RATE_LIMIT_POLICY = "SlidingWindowRateLimiter";

        public const string INTEGRATION_TEST_ENVIRONMENT = "IntegrationTest";

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddAuthorization();
            builder.Services.AddControllers(options =>
            {
                options.Filters.Add<InvariantExceptionFilter>();
                options.Filters.Add<ModelStateValidationFilter>();

                if (AppSettings.UserAllowList is HashSet<uint> allowList)
                    options.Filters.Add(new UserAllowListFilter(allowList));
            });
            builder.Services.AddHttpLogging(logging =>
            {
                logging.LoggingFields = HttpLoggingFields.All;
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
                    builder.Services.AddSwaggerGen(c =>
                    {
                        c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, Assembly.GetExecutingAssembly().GetName().Name + ".xml"));
                    });
                    break;
                }

                case "Staging":
                {
                    if (AppSettings.SentryDsn == null)
                    {
                        throw new InvalidOperationException("SENTRY_DSN environment variable not set. "
                                                            + "Please set the value of this variable to a valid Sentry DSN to use for logging events.");
                    }

                    break;
                }

                case "Production":
                {
                    if (AppSettings.SentryDsn == null)
                    {
                        throw new InvalidOperationException("SENTRY_DSN environment variable not set. "
                                                            + "Please set the value of this variable to a valid Sentry DSN to use for logging events.");
                    }

                    if (AppSettings.DatadogAgentHost == null)
                    {
                        throw new InvalidOperationException("DD_AGENT_HOST environment variable not set. "
                                                            + "Please set the value of this variable to a valid hostname of a Datadog agent.");
                    }

                    break;
                }
            }

            builder.Services.AddTransient<BeatmapPackagePatcher>();
            builder.Services.AddHttpClient();
            builder.Services.AddTransient<ISharedInterop, SharedInterop>();

            switch (AppSettings.StorageType)
            {
                case StorageType.Local:
                    builder.Services.AddTransient<IBeatmapStorage, LocalBeatmapStorage>();
                    break;

                case StorageType.S3:
                    builder.Services.AddSingleton<IBeatmapStorage, S3BeatmapStorage>();
                    break;

                default:
                {
                    if (builder.Environment.EnvironmentName == INTEGRATION_TEST_ENVIRONMENT)
                        break;

                    throw new InvalidOperationException($"BEATMAP_STORAGE_TYPE environment variable not set to a valid value (`{AppSettings.StorageType}`). "
                                                        + "The variable is used to choose the implementation of beatmap storage used. "
                                                        + "Valid values are:\n"
                                                        + "- `local` (requires setting `LOCAL_BEATMAP_STORAGE_PATH`),\n"
                                                        + "- `s3` (requires setting `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_CENTRAL_BUCKET_{NAME,REGION}`, `S3_BEATMAPS_BUCKET_{NAME,REGION}`)");
                }
            }

            if (AppSettings.PurgeBeatmapMirrorCaches)
                builder.Services.AddTransient<IMirrorService, MirrorService>();
            else
                builder.Services.AddTransient<IMirrorService, NoOpMirrorService>();

            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = (int)HttpStatusCode.TooManyRequests;
                options.AddPolicy(RATE_LIMIT_POLICY,
                    ctx =>
                    {
                        uint userId = ctx.User.GetUserId();

                        return RateLimitPartition.GetSlidingWindowLimiter(userId, _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = 6,
                            Window = TimeSpan.FromMinutes(1),
                            SegmentsPerWindow = 3,
                            QueueLimit = 0,
                        });
                    });
            });

            if (AppSettings.SentryDsn != null)
            {
                builder.Services.AddSingleton<ISentryUserFactory, UserFactory>();
                builder.WebHost.UseSentry(options =>
                {
                    options.Environment = builder.Environment.EnvironmentName;
                    options.SendDefaultPii = true;
                    options.Dsn = AppSettings.SentryDsn;
                });
            }

            if (AppSettings.DatadogAgentHost != null)
            {
                DogStatsd.Configure(new StatsdConfig
                {
                    StatsdServerName = AppSettings.DatadogAgentHost,
                    Prefix = "osu.server.beatmap-submission",
                    ConstantTags = new[]
                    {
                        $@"hostname:{Dns.GetHostName()}",
                        $@"startup:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
                    }
                });
            }

            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseReDoc();
            }

            app.UseAuthorization();
            app.MapControllers();
            app.UseHttpLogging();
            app.UseRateLimiter();

            app.Run();
        }
    }
}
