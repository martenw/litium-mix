# Fastly apple verify

Apple pay requires a verification file to be added on url `https://site.se/.well-known/apple-developer-merchantid-domain-association.txt`, this can be added directly in Fastly.

Solution below will respond with the content of file `apple_verify_site1.vcl` to any request where the URL contains the text `apple-developer-merchantid-domain-association`.

1. Copy `apple_verify_site1.vcl` to a new custom vcl in fastly and replace random-text with your verification file content
   1. Change _site1_ in the file name to the name of your site
1. Add content of recv.vcl to recv in Fastly
   1. Adjust the if statements to match your domains and set a unique header value for each domain
1. Add content of error.vcl to recv in Fastly
   1. Adjust if-statements to match headers set in `recv.vcl`
   1. Adjust include to match filenames set in the first step
