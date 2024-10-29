# osu-server-beatmap-submission

Handles submitting new and updating existing beatmaps to the [osu! website.](https://osu.ppy.sh/)

## Running

### Development

While the utility of it is limited, the project can be ran standalone in a development configuration.

1. Run `docker compose up` from the project root directory to bring up a MySQL which uses with the `osu-web` information schema.
2. Start the project in the `development` configuration.
3. The application will start at http://localhost:5089.
    - When interacting with endpoints which require authorisation, every request will use an auto-generated user ID that increments with every request.
      If you wish to use a fixed user ID, then provide a `user_id: ${YOUR_ID_HERE}` header in the request, wherein `YOUR_ID_HERE` is the desired user ID as a number.

### Staging

This configuration permits full-stack integration testing with other local osu! services.
To run the project in this configuration, you must have a configured [`osu-web` instance](https://github.com/ppy/osu-web/blob/master/SETUP.md).
Additionally, to test beatmap downloads, you may want to set up a local beatmap mirror; see [`osu-beatmap-mirror-docker-runtime`](https://github.com/ThePooN/osu-beatmap-mirror-docker-runtime) for more information.

1. [Set up a client key](https://github.com/ppy/osu-web/blob/master/SETUP.md#use-the-api-from-osu) that will be used to identify users. It should be the same client key that the osu!(lazer) client uses to communicate with `osu-web`.
2. In [`launchSettings.json`](osu.Server.BeatmapSubmission/Properties/launchSettings.json), in the `fullstack` configuration, alter the following environment variables:
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

| Envvar name                  | Description                                                                                                                                      |           Mandatory?            | Default value |
|:-----------------------------|:-------------------------------------------------------------------------------------------------------------------------------------------------|:-------------------------------:|:--------------|
| `DB_HOST`                    | Hostname under which the `osu-web` MySQL instance can be found.                                                                                  |              ❌ No               | `localhost`   |
| `DB_PORT`                    | Port under which the `osu-web` MySQL instance can be found.                                                                                      |              ❌ No               | `3306`        |
| `DB_USER`                    | Username to use when logging into the `osu-web` MySQL instance.                                                                                  |              ❌ No               | `root`        |
| `DB_PASS`                    | Password to use when logging into the `osu-web` MySQL instance.                                                                                  |              ❌ No               | `""`          |
| `DB_NAME`                    | Name of database to use on the indicated MySQL instance.                                                                                         |              ❌ No               | `osu`         |
| `JWT_VALID_AUDIENCE`         | The value of the `aud` claim to use when validating incoming JWTs. Should be set to the client ID assigned to osu! in the osu-web target deploy. | ⚠️ Yes, on staging / production | None          |
| `LOCAL_BEATMAP_STORAGE_PATH` | The path of a directory where the submitted beatmaps should reside.                                                                              |       ⚠️ Yes, on staging        | None          |
