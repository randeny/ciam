# Video Script — Native Auth + OBO → SAML SSO

**Target length:** 2–4 minutes (~450–600 spoken words)
**Format:** Narration (voice-over) with on-screen visuals and a live demo capture.

---

## Section 1 — Introduction (~45–60 sec)

**On screen:** Title card → customer logos (Mayo, UL, 3M) → split image of an "OIDC app" and a "SAML app" behind a single portal.

**Narration:**

> Many enterprise customers — like Mayo, UL, 3M, and others — still run critical line-of-business applications on **SAML**.
>
> Today, Azure AD B2C supports **custom policies** and **IdP-initiated sign-on**, which let these customers build a single portal that delivers single sign-on across **both OIDC and SAML** applications.
>
> On **Microsoft Entra External ID**, that experience hasn't been possible — for two reasons. First, there were gaps in **server-side orchestration**, and there was **no support for IdP-initiated sign-on**. Second, the **Native Authentication APIs** — the very APIs that let you build fully customized, embedded sign-in experiences — only supported **OIDC and OAuth 2.0**. There was no path to SAML.

---

## Section 2 — What changed (~20–30 sec)

**On screen:** Animated arrow — "Native Auth (OIDC)" + "OBO → SAML assertion" combining into "Custom SSO portal".

**Narration:**

> That changes with the introduction of **SAML token support through the On-Behalf-Of (OBO) flow**.
>
> Now you can **combine the Native Auth APIs with the OBO SAML exchange** — using a custom-built portal to sign users in natively, and then federate them into your existing SAML applications. One portal. Both protocols. No hosted login redirect.

---

## Section 3 — Demo (~60–90 sec)

**On screen:** *[Placeholder — insert screen capture of the running application here.]*

**Suggested capture flow to narrate over:**

1. Open the portal at `/portallogon-direct/`.
2. Sign in with username / password / OTP — highlight that this is a **native-auth SPA**, no redirect to a hosted login page.
3. On success, the app calls the backend and the SAML SP result page appears.
4. Show the result page confirming a **validated SAML assertion** and completed SSO.

**Narration (over the capture):**

> Here's the experience end to end. The user lands on a **custom portal** — this UI is fully ours, served from the app's own `wwwroot`.
>
> They sign in directly against Entra External ID using **native authentication** — username, password, and one-time passcode — with no redirect to a hosted page.
>
> The moment sign-in succeeds, the backend takes over, performs the token exchange behind the scenes, and the user lands on the **SAML application**, fully signed in. To the user, it's one seamless flow.

---

## Section 4 — Technical details (~45–60 sec)

**On screen:** The Mermaid sequence diagram from the README.

**Narration (walking the sequence):**

> Under the hood, here's what happens.
>
> One — the **browser SPA** signs the user in against **Entra External ID** using the Native Auth flow, and receives an **access token**.
>
> Two — the SPA posts that token to the **backend**, which acts as a **confidential OBO client**. It calls Entra with a `jwt-bearer` grant and requests a **SAML 2.0 token type**.
>
> Three — Entra returns a **SAML assertion**. The backend wraps it in a `samlp:Response` and **auto-POSTs** it to the SAML application's Assertion Consumer Service.
>
> Four — the SAML service provider **validates the signature, audience, lifetime, and replay**, then uses Post-Redirect-Get to show the result. Single sign-on is complete.

---

## Section 5 — Close (~10–15 sec)

**On screen:** Recap line — "Native Auth + OBO SAML = custom SSO across OIDC and SAML."

**Narration:**

> With Native Auth and the OBO SAML exchange, customers can finally build **fully branded, single-portal SSO** across their OIDC **and** SAML applications on Microsoft Entra External ID.
>
> Thanks for watching.

---

### Timing summary

| Section | Length |
| --- | --- |
| Introduction | 45–60 sec |
| What changed | 20–30 sec |
| Demo | 60–90 sec |
| Technical details | 45–60 sec |
| Close | 10–15 sec |
| **Total** | **~3–4 min** |
