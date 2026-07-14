using JMW.Discovery.Server.Api;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// Locks the API error contract: every <see cref="ApiError" /> factory emits an RFC 7807
/// ProblemDetails response carrying the framework-standard status/detail plus the machine-readable
/// <c>code</c> extension programmatic clients switch on. If this shape drifts, error consumers break
/// silently (the front-end reads <c>detail</c>; tooling reads <c>code</c>).
/// </summary>
public sealed class ApiErrorTests
{
    private static void AssertProblem(IResult result, int status, string code, string detail)
    {
        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(status, problem.StatusCode);
        Assert.Equal(status, problem.ProblemDetails.Status);
        Assert.Equal(detail, problem.ProblemDetails.Detail);
        Assert.True(
            problem.ProblemDetails.Extensions.TryGetValue("code", out object? actualCode),
            "ProblemDetails is missing the 'code' extension member."
        );
        Assert.Equal(code, actualCode);
    }

    [Fact]
    public void Problem_carries_status_detail_and_code_extension()
    {
        AssertProblem(
            ApiError.Problem(409, "sweep_in_progress", "A retention sweep is already running."),
            409,
            "sweep_in_progress",
            "A retention sweep is already running."
        );
    }

    [Fact]
    public void Shortcuts_pin_each_code_to_its_canonical_status()
    {
        AssertProblem(ApiError.InvalidCursor(), 422, "invalid_cursor", "Invalid pagination cursor.");
        AssertProblem(ApiError.NotFound("Device not found."), 404, "not_found", "Device not found.");
        AssertProblem(ApiError.InvalidId("Invalid device id."), 400, "invalid_id", "Invalid device id.");
        AssertProblem(ApiError.InvalidRequest("bad."), 400, "invalid_request", "bad.");
        AssertProblem(
            ApiError.InvalidBody("Request body is required."),
            400,
            "invalid_body",
            "Request body is required."
        );
        AssertProblem(ApiError.Conflict("conflict."), 409, "conflict", "conflict.");
    }
}