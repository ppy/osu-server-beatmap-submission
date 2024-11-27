// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Server.BeatmapSubmission.Models.API.Responses;

namespace osu.Server.BeatmapSubmission.Models
{
    /// <summary>
    /// Exceptions of this type are violations of various invariants enforced by this server,
    /// and their messages are assumed to be directly presentable to the user.
    /// </summary>
    public class InvariantException : Exception
    {
        public InvariantException(string message)
            : base(message)
        {
        }

        public ErrorResponse ToResponseObject() => new ErrorResponse(Message);
    }
}
