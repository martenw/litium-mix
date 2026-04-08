# Fastly apple verify

Apple pay requires a verification file to be added on url `https://site.se/.well-known/apple-developer-merchantid-domain-association.txt`, this can be added directly in Fastly.

Solution below will respond with the content of file `apple_verify_site1.vcl` to any request where the URL contains the text `apple-developer-merchantid-domain-association`.

1. Add content of `apple_verify_site1.vcl` as a new custom vcl
1. Add content of error.vcl to recv
1. Add content of recv.vcl to recv