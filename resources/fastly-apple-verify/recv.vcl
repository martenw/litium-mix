if ((req.request == "GET" || req.request == "HEAD")
  && req.url ~ "apple-developer-merchantid-domain-association") {

  if (req.http.host ~ "(?i)myfirstsite.com") {
    set req.http.X-ApplePay-Verify = "site1";

  } else if (req.http.host ~ "(?i)mysecondsite.com") {
    set req.http.X-ApplePay-Verify = "site2";

  } lse {
    error 404 "Host not Found";
    return (error);
  }

  error 200 "OK";
  return (error);
}