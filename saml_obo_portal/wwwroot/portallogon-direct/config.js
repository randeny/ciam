/*
 * Runtime configuration for the native-auth SPA.
 *
 * This file is intentionally NOT part of the bundled/minified JavaScript.
 * Edit the values below per environment (or generate this file at deploy time,
 * e.g. from Azure App Service settings) WITHOUT rebuilding or hand-editing the
 * minified bundle in assets/.
 *
 * The bundle reads these values at runtime and falls back to its built-in
 * defaults for any key left unset here.
 */
window.__APP_CONFIG__ = {
  // App A (native-auth SPA) Application (client) ID.
  clientId: "<app-A-spa-client-id>",

  // Your tenant's OpenID metadata URL.
  metadataUrl: "https://<native-auth-host>/<tenant-id>/v2.0/.well-known/openid-configuration",

  // App B's exposed scope (plus openid/offline_access).
  scope: "api://<app-B-client-id>/access openid offline_access",

  // Native-auth challenge types supported by the SPA.
  challengeType: "password oob redirect",

  // Backend OBO -> SAML endpoint (usually leave as-is).
  ssoEndpoint: "/portallogon/sso"
};
