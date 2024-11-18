// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using osu.Server.BeatmapSubmission.Configuration;
using osu.Server.QueueProcessor;

namespace osu.Server.BeatmapSubmission.Authentication
{
    /// <summary>
    /// Configures JWT authentication to be able to utilise JWTs issued by osu-web.
    /// </summary>
    /// <seealso href="https://github.com/ppy/osu-server-spectator/blob/9f8a00c0d85477e919382fe25bc2589e76166eec/osu.Server.Spectator/Authentication/ConfigureJwtBearerOptions.cs#L18"><c>ConfigureJwtBearerOptions</c> in <c>osu-server-spectator</c></seealso>
    public class OsuWebSharedJwtBearerOptions : IConfigureNamedOptions<JwtBearerOptions>
    {
        private readonly ILoggerFactory loggerFactory;

        public OsuWebSharedJwtBearerOptions(ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
        }

        public void Configure(JwtBearerOptions options)
        {
            var rsa = getKeyProvider();

            options.TokenValidationParameters = new TokenValidationParameters
            {
                IssuerSigningKey = new RsaSecurityKey(rsa),
                ValidAudience = AppSettings.JwtValidAudience,
                ValidateIssuer = false,
                ValidIssuer = "https://osu.ppy.sh/"
            };

            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var jwtToken = (JsonWebToken)context.SecurityToken;
                    int tokenUserId = int.Parse(jwtToken.Subject);

                    using (var db = DatabaseAccess.GetConnection())
                    {
                        // check expiry/revocation against database
                        int? userId = await db.QueryFirstOrDefaultAsync<int?>("SELECT `user_id` FROM `oauth_access_tokens` WHERE `revoked` = false AND `expires_at` > now() AND `id` = @id",
                            new { id = jwtToken.Id });

                        if (userId != tokenUserId)
                        {
                            loggerFactory.CreateLogger(nameof(OsuWebSharedJwtBearerOptions)).LogInformation("Token revoked or expired");
                            context.Fail("Token has expired or been revoked");
                        }
                    }
                },
            };
        }

        public void Configure(string? name, JwtBearerOptions options) => Configure(options);

        /// <summary>
        /// borrowed from https://stackoverflow.com/a/54323524
        /// </summary>
        private static RSACryptoServiceProvider getKeyProvider()
        {
            string key = File.ReadAllText("oauth-public.key");

            key = key.Replace("-----BEGIN PUBLIC KEY-----", "");
            key = key.Replace("-----END PUBLIC KEY-----", "");
            key = key.Replace("\n", "");

            byte[] keyBytes = Convert.FromBase64String(key);

            var asymmetricKeyParameter = PublicKeyFactory.CreateKey(keyBytes);
            var rsaKeyParameters = (RsaKeyParameters)asymmetricKeyParameter;
            var rsaParameters = new RSAParameters
            {
                Modulus = rsaKeyParameters.Modulus.ToByteArrayUnsigned(),
                Exponent = rsaKeyParameters.Exponent.ToByteArrayUnsigned()
            };

            var rsa = new RSACryptoServiceProvider();
            rsa.ImportParameters(rsaParameters);

            return rsa;
        }
    }
}
