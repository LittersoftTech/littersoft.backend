using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pawfront.Contracts.Services.PetSitter;
using Xunit;

namespace Pawfront.Api.Tests.Providers;

public class PetSitterEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PetSitterEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_ReturnsNotFound_WhenProviderNotRegistered()
    {
        var client = _factory.CreateClient();
        var providerId = Guid.NewGuid();

        var response = await client.GetAsync($"/api/v1/providers/{providerId}/services/pet-sitter/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RegisterPetHotel_ReturnsBadRequest_WhenInvalidPayload()
    {
        var client = _factory.CreateClient();
        var providerId = Guid.NewGuid();

        var invalid = new { PetHotelName = "", Address = "" };

        var response = await client.PostAsJsonAsync($"/api/v1/providers/{providerId}/services/pet-sitter/pet-hotel", invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
