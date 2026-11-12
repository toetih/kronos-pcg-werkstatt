using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PcgWeb;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

var host = builder.Build();

// Sprache vor dem ersten Zeichnen festlegen: Auswahl des Nutzers, sonst Browser-Sprache (Deutsch oder Englisch).
if (host.Services.GetRequiredService<Microsoft.JSInterop.IJSRuntime>() is Microsoft.JSInterop.IJSInProcessRuntime js)
{
    KronosPcg.Lang.English = js.Invoke<string>("pcgLanguage") == "en";
}

await host.RunAsync();
