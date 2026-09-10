using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
class FakeCodex {
    static void Main() {
        var json = new JavaScriptSerializer(); string line;
        while ((line = Console.ReadLine()) != null) {
            var r = json.Deserialize<Dictionary<string, object>>(line);
            if (!r.ContainsKey("id")) continue;
            string method = Convert.ToString(r["method"]);
            string mode = File.ReadAllText(Environment.GetEnvironmentVariable("GPT_USAGE_TEST_MODE")).Trim();
            object result = new {};
            if (method == "account/read") result = new { account = mode == "signed-out" ? null : new { type = "chatgpt", email = "test@example.invalid", planType = "test" } };
            if (method == "account/rateLimits/read") {
                if (mode == "offline" || mode == "expired") {
                    Console.WriteLine(json.Serialize(new { id = r["id"], error = new { code = -32000, message = mode == "offline" ? "HTTP 503 service unavailable" : "401 Unauthorized" } })); continue;
                }
                if (mode != "empty") result = new { rateLimits = new { primary = new { usedPercent = 43, windowDurationMins = 10080, resetsAt = 1789483807 }, secondary = (object)null } };
            }
            Console.WriteLine(json.Serialize(new { id = r["id"], result = result }));
        }
    }
}
