# Aerochat terms of service (self-hosted template)

> **Operator action required:** This is a terms template for the operator of an Aerochat deployment. Replace every `[OPERATOR ...]` field, adapt the terms to the actual service and jurisdiction, and obtain Brazilian legal advice before presenting them to users. It is not a contract offered by the source repository maintainers.

## 1. Operator, service, and acceptance

These Terms govern access to the Aerochat service operated by **[OPERATOR NAME]** at **[OPERATOR SERVICE URL]** (the **Service**). The operator's support and legal contact is **[OPERATOR CONTACT]**.

By accessing or using the Service, you agree to these Terms and the accompanying [Privacy Notice](PRIVACY.md). If you do not agree, do not access the Service. The operator may require an explicit acceptance step before enabling an account and should retain the version accepted where legally appropriate.

Aerochat is self-hosted software. These Terms apply to the operator's deployment, not to every copy of the source code or to an unrelated server run by another person. The source code and third-party components have separate licenses described in `LICENSE` and `THIRD_PARTY_NOTICES.md`.

## 2. What the Service provides

The Service may provide OAuth-only accounts, conversations, message history, real-time delivery, and optional GIF search or future RTC features. Features, limits, providers, and availability depend on the operator's configuration. The operator may run the client in offline/demo mode, disable a provider, or suspend a feature without promising that every feature will be available at all times.

There is no password account system in the current Aerochat contract. Account access uses an enabled OAuth provider such as Google, GitHub, or Discord. Provider terms, privacy rules, age requirements, and account decisions also apply to the provider account.

## 3. Eligibility and age

You may use the Service only if you can form a binding agreement where you live and you meet the minimum age and other eligibility requirements of the OAuth provider you use. Do not use the Service if the provider would not permit your account or if applicable Brazilian law requires a different authorization or consent process. The operator may restrict or refuse access where required to protect children, users, or the Service.

Parents or legal guardians should review the operator's deployment and privacy practices before permitting a minor to use it. The operator must add any age-specific rules required by its service, audience, and legal advice here: **[OPERATOR AGE POLICY]**.

## 4. Accounts and authentication

You must use an OAuth account you are authorized to use and provide accurate information to the provider. Do not impersonate another person or create an account to evade a suspension. Keep your browser, device, and session credentials secure, and promptly notify **[OPERATOR CONTACT]** of suspected unauthorized access.

The operator may rely on the provider's identity response, including a provider ID, display name, and an optional verified email. An email address is not a password or an independent recovery mechanism. Never send a password, OAuth client secret, session signing key, or arbitrary API token in a chat message, issue, or support request.

The operator may suspend or terminate an account when required for security, abuse prevention, legal compliance, provider requirements, or a breach of these Terms. The operator should use a fair and documented process where practical, but emergency action may be taken first when delay creates material risk.

## 5. Acceptable use

You must use the Service lawfully and respect other people. You must not:

- harass, threaten, stalk, dox, exploit, or target another person or group;
- send unlawful, fraudulent, defamatory, hateful, sexually explicit, exploitative, or privacy-invasive material;
- upload malware, ransomware, credential theft tools, or content intended to damage another device or network;
- probe, scan, reverse engineer, bypass authentication, defeat rate limits, or access another user's conversations or account;
- impersonate a person, provider, operator, or moderator, or misrepresent the origin of content;
- use the Service for spam, automated bulk messaging, scraping, or commercial activity that the operator has not authorized;
- infringe copyright, trademark, publicity, privacy, or other rights;
- attempt to disrupt the Service, consume unreasonable resources, or interfere with another user's use; or
- use optional GIF/RTC features in a way that violates applicable provider/API terms or the operator's safety policy.

The operator may publish additional community rules at **[OPERATOR COMMUNITY RULES]**. Those rules form part of these Terms only after the operator makes them available and identifies their effective date.

## 6. User content and communications

You retain rights you have in the messages and other content you submit. You grant **[OPERATOR NAME]** a limited, non-exclusive, worldwide license to host, store, reproduce, transmit, display to intended conversation participants, back up, moderate, and technically transform that content only as reasonably necessary to operate, secure, troubleshoot, and improve the Service or comply with law. This license ends for active service processing when the content is deleted, subject to soft-deleted records, backups, legal holds, incident records, and other retention described in the [Privacy Notice](PRIVACY.md).

You are responsible for having the rights and permissions needed for your content and for the consequences of sending it. Do not submit passwords, OAuth secrets, payment-card data, government identifiers, or sensitive personal data unless the operator has explicitly documented a safe and lawful need. Messages may be copied, forwarded, reported, retained, or disclosed as described in the Privacy Notice and applicable law.

The operator does not endorse user content merely because it is stored or delivered. The operator may remove, restrict, label, preserve, or disclose content when it reasonably believes that doing so is necessary for safety, moderation, security, legal compliance, or the operation of the Service.

## 7. Third-party providers and Tenor

OAuth authentication depends on the provider selected by the operator. Google, GitHub, Discord, and other enabled providers are independent services with their own terms and privacy policies; an outage, suspension, or policy decision by a provider may prevent login.

If the operator enables Tenor GIF search, search queries and necessary request metadata are sent to Tenor. The operator must keep the API key server-side, use the required content-safety settings, preserve Tenor's links/branding, and display the required Tenor attribution where GIF results are shown. Tenor's API terms prohibit altering or reordering search results and impose additional content restrictions.[1][2]

The operator may disable or replace a third-party integration at any time. Third-party content and services are not guaranteed by the Aerochat source repository or by these Terms.

## 8. Privacy and LGPD

The operator is generally the controller for the personal data processed by its deployment and must publish a deployment-specific privacy notice. The Brazilian LGPD (Lei nº 13.709/2018) governs personal-data processing in the circumstances defined by that law.[3] Read [PRIVACY.md](PRIVACY.md) for the operator's data inventory, retention, third-party disclosures, contact route, and the current roadmap status of `DELETE /me`.

Nothing in these Terms removes rights or duties that cannot lawfully be waived. The operator must select and document its legal bases, respond to applicable data-subject requests, protect data, and update the Privacy Notice when its deployment changes.

## 9. Moderation, suspension, and termination

The operator may investigate reports, moderate content, limit features, suspend accounts, terminate access, or shut down the Service. Grounds may include violation of these Terms or community rules, provider restrictions, suspected compromise, abuse, legal requests, safety risks, resource abuse, or a need to protect the operator or other users.

Unless prohibited by law or unsafe in context, the operator should provide notice and a practical route to appeal at **[OPERATOR APPEAL CHANNEL]**. The operator may act without advance notice in emergencies and may preserve or disclose information when required by law. Termination does not erase records that the operator must retain under its published policy or a legal obligation.

## 10. Changes and availability

The operator may change the Service, these Terms, or the Privacy Notice as the deployment evolves. Material changes should identify an effective date and be communicated through **[OPERATOR NOTICE CHANNEL]**. Continued use after the effective date means acceptance to the extent permitted by law; if you do not accept a material change, stop using the Service and contact the operator about account closure.

The Service is provided without an uptime commitment unless the operator separately promises one in writing. Maintenance, provider outages, network failures, data loss, abuse, security incidents, and force majeure may interrupt or end access. Operators should maintain backups and publish their recovery expectations rather than promising that message history is permanent.

## 11. Disclaimer of warranties

To the maximum extent permitted by applicable law, the Service and Aerochat software are provided **as is** and **as available**, without warranties of any kind, express or implied, including warranties of merchantability, fitness for a particular purpose, non-infringement, availability, accuracy, security, or uninterrupted operation. The operator does not warrant that the Service will meet every need, retain every message, or be free from defects, malicious content, or outages.

Nothing in this section limits a warranty, guarantee, or consumer protection that Brazilian law makes mandatory or that cannot legally be excluded.

## 12. Limitation of liability

To the maximum extent permitted by applicable law, **[OPERATOR NAME]** and its maintainers, volunteers, suppliers, and service providers will not be liable for indirect, incidental, special, consequential, exemplary, or punitive damages, or for lost profits, goodwill, data, access, or business interruption, arising from or related to the Service, user content, third-party services, or these Terms, even if advised that such damages were possible.

To the maximum extent permitted by applicable law, the aggregate liability for direct damages arising from the Service will be limited to the amount the user paid the operator for the Service during the **[OPERATOR LIABILITY PERIOD]** immediately preceding the event giving rise to the claim, or the minimum amount required by law if that limitation is not enforceable. This section does not exclude liability for fraud, willful misconduct, death or personal injury where exclusion is prohibited, violations that cannot be limited, or any other liability that applicable law requires the operator to accept.

## 13. Governing law and forum option

Choose and complete one option before publication:

- **Brazilian operator option:** These Terms are governed by the laws of Brazil. Subject to mandatory consumer, privacy, and other protective rules, the parties may submit disputes to the courts of **[OPERATOR CITY/STATE]**, Brazil.
- **Other operator option:** These Terms are governed by the laws of **[OPERATOR JURISDICTION]**, and disputes may be brought in the courts of **[OPERATOR FORUM]**, subject to any mandatory rules that apply to the user.

The operator must not use this clause to remove a forum, consumer protection, or data-protection right that Brazilian law makes mandatory.

## 14. Contact

Questions, abuse reports, privacy requests, and account appeals should be sent to **[OPERATOR CONTACT]**. The operator should state its response expectations and escalation route here: **[OPERATOR SUPPORT AND ESCALATION POLICY]**.

If a provision is held unenforceable, it should be limited or replaced only to the extent necessary, and the remaining provisions should continue to operate. These Terms, together with the deployment's written policies expressly incorporated into them, are the operator's entire agreement for the Service unless the operator publishes a separate agreement.

## Sources

[1] [Lei nº 13.709/2018 (LGPD), Planalto](https://www.planalto.gov.br/ccivil_03/_ato2015-2018/2018/lei/l13709.htm)

[2] [Tenor API Terms of Service](https://developers.google.com/tenor/guides/api-terms)

[3] [Tenor API documentation: content filtering and attribution](https://tenor.com/gifapi/documentation)
