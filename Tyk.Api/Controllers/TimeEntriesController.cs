using Microsoft.AspNetCore.Mvc;
using Tyk.Application.Interfaces;

namespace TimeTracker.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TimeEntriesController(ITimeEntryRepository repository) : ControllerBase
{
    [HttpGet("{userId}")]
    public async Task<ActionResult<IEnumerable<TimeEntry>>> GetByUser(
        long userId,
        [FromQuery] DateTime from,
        [FromQuery] DateTime to)
    {
        return Ok(await repository.GetUserEntriesAsync(userId, from, to));
    }
}