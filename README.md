# osu-server-beatmap-submission

Handles submitting new and updating existing beatmaps to the [osu! website.](https://osu.ppy.sh/)

## Running

### Development

This configuration permits full-stack integration testing with other local osu! services.
To run the project in this configuration, you must have a configured [`osu-web` instance](https://github.com/ppy/osu-web/blob/master/SETUP.md).
Additionally, to test beatmap downloads, you may want to set up a local beatmap mirror; see [`osu-beatmap-mirror-docker-runtime`](https://github.com/ThePooN/osu-beatmap-mirror-docker-runtime) for more information.

1. [Set up a client key](https://github.com/ppy/osu-web/blob/master/SETUP.md#use-the-api-from-osu) that will be used to identify users. It should be the same client key that the osu!(lazer) client uses to communicate with `osu-web`.
2. In [`launchSettings.json`](osu.Server.BeatmapSubmission/Properties/launchSettings.json), in the `fullstack` configuration, set the following environment variables:
   - Set `JWT_VALID_AUDIENCE` to the client ID assigned to the key created in step (1).
   - Set `LOCAL_BEATMAP_STORAGE_PATH` to a directory where submitted beatmaps should be placed.
3. Copy the `osu-web` OAuth public key to the `osu.Server.BeatmapSubmission` project root:
   ```
   $ cp osu-web/storage/oauth-public.key osu-server-beatmap-submission/osu.Server.BeatmapSubmission/oauth-public.key
   ```
4. Start the project in the `fullstack` configuration.
5. The application will start at http://localhost:5089.
   - When interacting with endpoints which require authorisation, every request must include an `Authorization: Bearer <TOKEN>` header. That token must be a token issued by `osu-web` for the client key set up in step (1).

## Environment variables

For advanced testing purposes.

| Envvar name                  | Description                                                                                                                                        |            Mandatory?            | Default value |
|:-----------------------------|:---------------------------------------------------------------------------------------------------------------------------------------------------|:--------------------------------:|:--------------|
| `DB_HOST`                    | Hostname under which the `osu-web` MySQL instance can be found.                                                                                    |               ❌ No               | `localhost`   |
| `DB_PORT`                    | Port under which the `osu-web` MySQL instance can be found.                                                                                        |               ❌ No               | `3306`        |
| `DB_USER`                    | Username to use when logging into the `osu-web` MySQL instance.                                                                                    |               ❌ No               | `root`        |
| `DB_PASS`                    | Password to use when logging into the `osu-web` MySQL instance.                                                                                    |               ❌ No               | `""`          |
| `DB_NAME`                    | Name of database to use on the indicated MySQL instance.                                                                                           |               ❌ No               | `osu`         |
| `JWT_VALID_AUDIENCE`         | The value of the `aud` claim to use when validating incoming JWTs. Should be set to the client ID assigned to osu! in the `osu-web` target deploy. |              ✔️ Yes              | None          |
| `LOCAL_BEATMAP_STORAGE_PATH` | The path of a directory where the submitted beatmaps should reside.                                                                                |     ⚠️ In development config     | None          |
| `LEGACY_IO_DOMAIN`           | The root domain to which legacy IO requests should be directed to.                                                                                 |              ✔️ Yes              | None          |
| `SHARED_INTEROP_SECRET`      | The interop secret used for legacy IO requests. Value should match same environment variable in target `osu-web` instance.                         |              ✔️ Yes              | None          |
| `S3_ACCESS_KEY`              | A valid Amazon S3 access key ID.                                                                                                                   | ⚠ In staging/production configs  | None          |
| `S3_SECRET_KEY`              | The secret key corresponding to the `S3_ACCESS_KEY`.                                                                                               | ⚠ In staging/production configs  | None          |
| `S3_BUCKET_NAME`             | The name of the S3 bucket to use for beatmap storage.                                                                                              | ⚠ In staging/production configs  | None          | 
| `SENTRY_DSN`                 | A valid Sentry DSN to use for logging application events.                                                                                          | ⚠ In staging/production configs  | None          | 

