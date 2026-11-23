using Microsoft.AspNetCore.Mvc;

namespace MeetingActions.Api.Controllers;

/// <summary>Serves local demonstration data to the UI.</summary>
[ApiController]
[Route("api/sample-transcript")]
public sealed class SamplesController : ControllerBase
{
    /// <summary>Returns the bundled sample meeting transcript.</summary>
    [HttpGet]
    public async Task<ActionResult<string>> Get(CancellationToken cancellationToken)
    {
        var transcriptPath = Path.Combine(AppContext.BaseDirectory, "meeting-transcript.txt");
        if (!System.IO.File.Exists(transcriptPath))
        {
            return NotFound("Sample transcript file was not found.");
        }

        return Content(await System.IO.File.ReadAllTextAsync(transcriptPath, cancellationToken), "text/plain");
    }
}
