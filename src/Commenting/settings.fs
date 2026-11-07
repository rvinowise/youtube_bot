namespace commenting

open System.Linq
open Microsoft.Extensions.Configuration


module Settings =

    let private configuration =
        ConfigurationBuilder()
            .SetBasePath(System.IO.Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional = false, reloadOnChange = true)
            .Build()

    // Browser settings
    let userDataDir : string = 
        configuration.GetValue<string>("browser:profiles_dir", "utils/browser_profiles")
        
    let profiles : string array =
        configuration.GetSection("browser:profiles").Get<string[]>()
        
    let isHeadless : bool = 
        configuration.GetValue<bool>("browser:headless", false)

    // Commenting behavior
    let delayMinSeconds : int = 
        configuration.GetValue<int>("commenting:delay_min_seconds", 5)
        
    let delayMaxSeconds : int = 
        configuration.GetValue<int>("commenting:delay_max_seconds", 15)