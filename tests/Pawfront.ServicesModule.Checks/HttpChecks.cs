using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pawfront.Application.Bookings;
using Pawfront.Contracts.Bookings;

static class HttpChecks
{
    public static async Task Run(Guid provider, IBookingSqlStore store, INightStayBookingSqlStore nightStore,
        Action<bool,string> check, Func<bool,string,DateTime,Task<Guid>> seed)
    {
        var types = new[] { "BookingEndpoints", "NightStayBookingEndpoints" }
            .Select(n=>Assembly.Load("Pawfront.Api").GetType($"Pawfront.Api.Endpoints.{n}")!).ToArray();
        var builder=WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        foreach(var serviceType in types.SelectMany(t=>t.GetMethods(BindingFlags.Static|BindingFlags.NonPublic))
            .SelectMany(m=>m.GetParameters()).Select(p=>p.ParameterType)
            .Where(t=>t.IsInterface && t.Namespace?.StartsWith("Pawfront.")==true).Distinct())
        {
            var instance=DispatchProxy.Create(serviceType,typeof(EndpointProxy));
            ((EndpointProxy)instance).Call=(method,args)=>
            {
                if(method.Name=="StartJobAsync")
                {
                    var c=(StartBookingCommand)args![0]!;
                    check(c.WaitForCustomer,"Wait endpoint forwards wait action rather than normal start");
                    return serviceType==typeof(IBookingService)
                        ? (object)store.StartJobAsync(c.BookingId,c.ProviderId,"999999",10,c.Location,default,c.WaitForCustomer)
                        : nightStore.StartJobAsync(c.BookingId,c.ProviderId,"999999",10,c.Location,default,c.WaitForCustomer);
                }
                throw new NotSupportedException(method.Name);
            };
            builder.Services.AddSingleton(serviceType,instance);
        }
        await using var app=builder.Build();
        types[0].GetMethod("MapBookingEndpoints")!.Invoke(null,[app]);
        types[1].GetMethod("MapProviderNightStayBookingEndpoints")!.Invoke(null,[app]);
        await app.StartAsync(); var client=app.GetTestClient();
        foreach(var night in new[]{false,true})
        {
            var id=await seed(night,"START_JOB",DateTime.UtcNow.Date);
            var path=$"/providers/{provider}/{(night?"night-stay-bookings":"bookings")}/{id}/wait-for-customer";
            for(var i=0;i<2;i++)
            {
                var response=await client.PostAsJsonAsync(path,new StartJobRequest(new(47,8,null,null)));
                check(response.StatusCode==HttpStatusCode.OK,$"{path} retry {i} returns HTTP 200");
            }
        }
        await app.StopAsync();
    }
    public class EndpointProxy:DispatchProxy
    {
        public Func<MethodInfo,object?[]?,object?> Call=null!;
        protected override object? Invoke(MethodInfo? targetMethod,object?[]? args)=>Call(targetMethod!,args);
    }
}
