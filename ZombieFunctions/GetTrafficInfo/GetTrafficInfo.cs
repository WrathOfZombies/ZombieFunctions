using System;
using System.Linq;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using System.Collections.Specialized;
using System.Net.Http;
using Microsoft.WindowsAzure.Storage.Table;

namespace ZombieFunctions.GetTraffic
{
    public static class GetTrafficInfo
    {
        [FunctionName("GetTrafficInfo")]
        public static async Task Run(
            [TimerTrigger("0 */30 * * * *")] TimerInfo myTimer,
            [Table("TrafficInfo", Connection = "TrafficInfoTableKey")] ICollector<Route> routesTableBinding,
            TraceWriter log)
        {
            const string baseUrl = "https://maps.googleapis.com/maps/api/directions/json";
            (string origin, string destination) = GetOriginAndDestination();

            if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(destination))
            {
                log.Info($"Skipping computation as it is not in travel hours: {DateTime.UtcNow}");
                return;
            }

            var paramters = new NameValueCollection()
            {
                ["origin"] = origin,
                ["destination"] = destination,
                ["departure_time"] = "now",
                ["alternatives"] = "true",
                ["key"] = Environment.GetEnvironmentVariable("GoogleMapsAppKey", EnvironmentVariableTarget.Process),
                ["traffic_model"] = "best_guess"
            };

            var query = String.Join("&", paramters.AllKeys.Select(key => $"{key}={paramters[key]}"));

            using (var client = new HttpClient())
            {
                try
                {
                    var url = $"{baseUrl}?{query}";
                    var response = await client.GetAsync(url);
                    var json = await response.Content.ReadAsStringAsync();

                    var processedData = await GetAllRoutes(json);
                    var list = processedData.ToList();

                    list.ForEach(route =>
                    {
                        routesTableBinding.Add(route);
                        log.Info($"Route takes {route.DurationInTrafficText} to reach.");
                    });

                    log.Info($"Number of routes processed: {list.Count}");
                }
                catch (Exception error)
                {
                    log.Error(error.Message, error);
                }
            }
        }

        public static (string origin, string destination) GetOriginAndDestination()
        {
            var home = Environment.GetEnvironmentVariable("HomeLocation", EnvironmentVariableTarget.Process);
            var work = Environment.GetEnvironmentVariable("WorkLocation", EnvironmentVariableTarget.Process);

            string origin, destination;
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            var pacificNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            var hours = pacificNow.TimeOfDay.Hours;
            if (hours >= 6 && hours <= 12)
            {
                /* Assume you go to work between 6:00 to 12:00 */

                origin = home;
                destination = work;
            }
            else if (hours > 15 && hours <= 19)
            {
                /* Assume you return home between 15:00 to 19:00 */

                origin = work;
                destination = home;
            }
            else
            {
                return (null, null);
            }

            return (origin, destination);
        }

        public static Task<IEnumerable<Route>> GetAllRoutes(string json)
        {
            return Task.Run(() =>
            {
                return JObject.Parse(json).SelectToken("routes")
                    .Select(route =>
                    {
                        var summary = (string)route.SelectToken("summary");
                        var leg = route.SelectToken("legs").FirstOrDefault();
                        return new
                        {
                            summary = summary,
                            leg = leg
                        };
                    })
                    .Select(item => new Route
                    {
                        PartitionKey = "Routes",
                        RowKey = Guid.NewGuid().ToString(),
                        Summary = item.summary,
                        DistanceText = (string)item.leg.SelectToken("distance.text"),
                        DistanceValue = (int)item.leg.SelectToken("distance.value"),
                        DurationText = (string)item.leg.SelectToken("duration.text"),
                        DurationValue = (int)item.leg.SelectToken("duration.value"),
                        DurationInTrafficText = (string)item.leg.SelectToken("duration_in_traffic.text"),
                        DurationInTrafficValue = (int)item.leg.SelectToken("duration_in_traffic.value"),
                        EndAddress = (string)item.leg.SelectToken("end_address"),
                        EndLocationLat = (string)item.leg.SelectToken("end_location.lat"),
                        EndLocationLng = (string)item.leg.SelectToken("end_location.lng"),
                        StartAddress = (string)item.leg.SelectToken("start_address"),
                        StartLocationLat = (string)item.leg.SelectToken("start_location.lat"),
                        StartLocationLng = (string)item.leg.SelectToken("start_location.lng")
                    });
            });
        }
    }

    public class Route : TableEntity
    {
        public string Summary { get; set; }

        public int DistanceValue { get; set; }
        public string DistanceText { get; set; }

        public int DurationValue { get; set; }
        public string DurationText { get; set; }

        public int DurationInTrafficValue { get; set; }
        public string DurationInTrafficText { get; set; }

        public string StartLocationLat { get; set; }
        public string StartLocationLng { get; set; }
        public string StartAddress { get; set; }

        public string EndLocationLat { get; set; }
        public string EndLocationLng { get; set; }
        public string EndAddress { get; set; }
    }
}
