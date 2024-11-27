// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace osu.Server.BeatmapSubmission.Models.API.Responses
{
    /// <summary>
    /// Response type issued in case of an error that is directly presentable to the user.
    /// </summary>
    public class ErrorResponse
    {
        [JsonPropertyName("error")]
        public string Error { get; }

        public ErrorResponse(string error)
        {
            Error = error;
        }

        public IActionResult ToActionResult() => new UnprocessableEntityObjectResult(this);
    }
}
