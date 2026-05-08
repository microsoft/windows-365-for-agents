// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Core;
using Microsoft.Agents.Core.Models;
using OpenWeatherMapSharp;
using OpenWeatherMapSharp.Models;
using OpenWeatherMapSharp.Models.Enums;
using System.ComponentModel;

namespace Microsoft.W365APlaygroundAgent.Tools
{
    /// <summary>
    /// Sample tool that looks up current weather and forecast via the OpenWeatherMap API.
    /// Demonstrates the local-tool pattern: a regular C# class with [Description]-annotated
    /// methods registered through <c>AIFunctionFactory.Create</c> in <c>MyAgent</c>.
    /// </summary>
    public class WeatherLookupTool(ITurnContext turnContext, IConfiguration configuration, ILogger<WeatherLookupTool> logger)
    {
        private const string OpenWeatherKeyConfigPath = "OpenWeatherApiKey";

        [Description("Retrieves the Current weather for a location, location is a city name")]
        public async Task<WeatherRoot?> GetCurrentWeatherForLocation(string location, string state)
        {
            var coords = await ResolveLocationAsync(location, state, "Looking up the Current Weather").ConfigureAwait(false);
            if (coords is null) return null;

            await NotifyUserAsync($"Fetching Current Weather for {location}").ConfigureAwait(false);
            var weather = await GetService().GetWeatherAsync(coords.Value.Latitude, coords.Value.Longitude, unit: Unit.Imperial).ConfigureAwait(false);
            if (!weather.IsSuccess)
            {
                logger.LogWarning("OpenWeather GetWeatherAsync failed for ({Lat}, {Lon}): {Error}", coords.Value.Latitude, coords.Value.Longitude, weather.Error);
                return null;
            }
            return weather.Response;
        }

        [Description("Retrieves the Weather forecast for a location, location is a city name")]
        public async Task<List<ForecastItem>?> GetWeatherForecastForLocation(string location, string state)
        {
            var coords = await ResolveLocationAsync(location, state, "Looking up the Weather Forecast").ConfigureAwait(false);
            if (coords is null) return null;

            await NotifyUserAsync($"Fetching Weather Forecast for {location}").ConfigureAwait(false);
            var forecast = await GetService().GetForecastAsync(coords.Value.Latitude, coords.Value.Longitude, unit: Unit.Imperial).ConfigureAwait(false);
            if (!forecast.IsSuccess)
            {
                logger.LogWarning("OpenWeather GetForecastAsync failed for ({Lat}, {Lon}): {Error}", coords.Value.Latitude, coords.Value.Longitude, forecast.Error);
                return null;
            }
            return forecast.Response.Items;
        }

        /// <summary>
        /// Resolves a city/state pair to lat/lon coordinates and notifies the user via the channel-appropriate
        /// streaming or message API. Returns <c>null</c> when the location can't be resolved (the model will
        /// see a null result in the function output and can ask the user to clarify).
        /// </summary>
        private async Task<(double Latitude, double Longitude)?> ResolveLocationAsync(string location, string state, string statusPrefix)
        {
            AssertionHelpers.ThrowIfNull(turnContext, nameof(turnContext));

            await NotifyUserAsync($"{statusPrefix} in {location}").ConfigureAwait(false);
            logger.LogInformation("{Status} in {Location}, {State}", statusPrefix, location, state);

            var lookup = await GetService().GetLocationByNameAsync($"{location},{state}").ConfigureAwait(false);
            if (lookup is null || !lookup.IsSuccess)
            {
                logger.LogWarning("OpenWeather location lookup failed for {Location}, {State}: {Error}", location, state, lookup?.Error ?? "(no result)");
                await NotifyUserAsync($"Sorry, I couldn't look up the location {location}, {state}.").ConfigureAwait(false);
                return null;
            }

            var info = lookup.Response.FirstOrDefault();
            if (info is null)
            {
                logger.LogWarning("OpenWeather location lookup returned no candidates for {Location}, {State}.", location, state);
                await NotifyUserAsync($"Sorry, I couldn't resolve the location {location}, {state}.").ConfigureAwait(false);
                return null;
            }

            return (info.Latitude, info.Longitude);
        }

        /// <summary>
        /// Constructs an OpenWeather client. Throws a clear error if the API key isn't configured —
        /// for a sample, an early diagnostic beats a confusing API failure later.
        /// </summary>
        private OpenWeatherMapService GetService()
        {
            var apiKey = configuration[OpenWeatherKeyConfigPath];
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new InvalidOperationException(
                    $"{OpenWeatherKeyConfigPath} is not configured. " +
                    $"Set via 'dotnet user-secrets set \"{OpenWeatherKeyConfigPath}\" \"<key>\"' (local dev) " +
                    $"or in appsettings.json / Azure App Settings (production). " +
                    $"Get a free key at https://openweathermap.org/price.");
            }
            return new OpenWeatherMapService(apiKey);
        }

        /// <summary>
        /// Sends a status message to the user via the channel-appropriate API: streaming for Teams/agentic
        /// channels, direct message for WebChat (which doesn't render streaming chunks the same way).
        /// </summary>
        private async Task NotifyUserAsync(string message)
        {
            if (!turnContext.Activity.ChannelId.Channel!.Contains(Channels.Webchat))
                await turnContext.StreamingResponse.QueueInformativeUpdateAsync(message).ConfigureAwait(false);
            else
                await turnContext.SendActivityAsync(MessageFactory.Text(message)).ConfigureAwait(false);
        }
    }
}
