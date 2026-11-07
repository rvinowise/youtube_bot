namespace commenting

open Microsoft.Playwright
open Settings


module Browser =    

    let launch_browser () =
        let playwright = Playwright.CreateAsync().Result
        
        // 1. Get the first profile name from the list (e.g., "firefox:default-release")
        let rawProfile = Settings.profiles |> Array.head
        
        // 2. Extract just the name after the colon (e.g., "default-release")
        let profileName = rawProfile.Split(':').[1] 
        
        // 3. Combine the directory and the profile name into a full path
        let fullProfilePath = System.IO.Path.Combine(Settings.userDataDir, profileName)

        let context = 
            playwright.Firefox.LaunchPersistentContextAsync(
                fullProfilePath, // Pass the full path here
                BrowserTypeLaunchPersistentContextOptions(
                    Headless = Settings.isHeadless
                    // Remove the 'Profile =' line entirely, it doesn't exist in .NET Playwright
                )
            ).Result
            
        context

    let open_page (url: string) (context: IBrowserContext) =
        let page = context.NewPageAsync().Result
        page.GotoAsync(url).Wait()
        page