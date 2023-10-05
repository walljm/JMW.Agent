using Microsoft.AspNetCore.ApiAuthorization.IdentityServer;
using Microsoft.AspNetCore.Mvc;

namespace JMW.Agent;

public static class OidcEndpoints
{
    public static IEndpointRouteBuilder AddOidcEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("_configuration/{clientId}", GetClientRequestParameters);

        return builder;
    }

    private static IResult GetClientRequestParameters(
        [FromServices] IClientRequestParametersProvider ClientRequestParametersProvider,
        HttpContext context,
        [FromRoute] string clientId
    )
    {
        var parameters = ClientRequestParametersProvider.GetClientParameters(context, clientId);
        return Results.Ok(parameters);
    }
}
