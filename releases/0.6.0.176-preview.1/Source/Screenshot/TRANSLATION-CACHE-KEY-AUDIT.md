# Translation Cache Key Audit

The translation cache key is SHA-256 over a canonical identity containing:

1. canonical text/ID hash;
2. normalized target language;
3. translation style;
4. custom prompt hash;
5. explicit provider identity;
6. normalized Base URL/API URL;
7. model;
8. ID/number/variable preservation switches;
9. prompt-builder version.

The API key is deliberately excluded and never embedded in the cache key or diagnostic snapshot.

Verified behavior:

- identical identity: hit;
- target/style/custom prompt/model/Base URL change: miss;
- API key change alone: same cache identity;
- equivalent Base URL casing/trailing slash: same normalized identity.

