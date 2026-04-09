if (obj.status == 200 && req.http.X-ApplePay-Verify) {
  set obj.http.Content-Type = "text/plain; charset=utf-8";
  set obj.http.Cache-Control = "public, max-age=300";

  if (req.http.X-ApplePay-Verify == "site1") {
    include "apple_verify_site1.vcl";
    return (deliver);
  }
  
  if (req.http.X-ApplePay-Verify == "site2") {
    include "apple_verify_site2.vcl";
    return (deliver);
  }

  # Safety fallback
  set obj.status = 404;
  set obj.response = "Apple verify not Found";
  synthetic {"Apple verify not Found"};
  return (deliver);
}