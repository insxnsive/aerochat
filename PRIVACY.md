# Aerochat privacy notice (self-hosted template)

> **Operator action required:** This document is a template for the person or organization that runs an Aerochat server. Replace the `[OPERATOR ...]` fields, confirm the actual deployment, and obtain local legal advice before publishing it to users. The operator of a deployment—not the upstream source repository—is normally the party deciding why and how that deployment processes personal data.

## Resumo em português (Brasil)

A Lei Geral de Proteção de Dados Pessoais (LGPD), Lei nº 13.709/2018, protege dados pessoais tratados inclusive em meios digitais e define como controlador quem toma as decisões sobre esse tratamento.[8]

### Quem é o controlador

Na sua instância do Aerochat, **[OPERATOR NAME]** é o controlador dos dados pessoais tratados pelo servidor. Para dúvidas e solicitações, use **[OPERATOR PRIVACY CONTACT]**. Se aplicável, o encarregado/DPO é **[OPERATOR ENCARREGADO CONTACT]**. O Aerochat é software auto-hospedado: a política, a base legal, a segurança, os prazos de retenção e o atendimento aos titulares precisam ser definidos pelo operador.

### Quais dados são tratados

O servidor pode armazenar:

- o provedor de login (Google, GitHub ou Discord) e o identificador da conta nesse provedor;
- o nome de exibição;
- o e-mail somente quando o provedor informar expressamente que ele foi verificado;
- conversas, participantes, mensagens, edições e exclusões realizadas no serviço, em banco SQLite administrado pelo operador.

### O que não é armazenado

O Aerochat não possui cadastro por senha e não armazena senhas, hashes de senha, perguntas de segurança ou fluxo de redefinição de senha. O login é feito por OAuth. Tokens OAuth de acesso/atualização são usados somente no fluxo do servidor e não são persistidos como credenciais da conta; a credencial de sessão do Aerochat é um JWT assinado pelo próprio servidor. Segredos de cliente e credenciais do provedor ficam na configuração do servidor, e os tokens de sessão nunca devem ser registrados em logs.

### Retenção e exclusão

O operador configura a retenção. Uma mensagem excluída pode permanecer como exclusão lógica (soft delete) no banco e em cópias de segurança até a rotina de expurgo definida pelo operador. O endpoint `DELETE /me` para solicitar a exclusão da conta está planejado, mas ainda é um item de roadmap; até sua implementação, solicite a exclusão por **[OPERATOR PRIVACY CONTACT]** e o operador deverá executar um processo manual verificável.

### Compartilhamento com terceiros

Google, GitHub e Discord participam do fluxo OAuth e têm suas próprias políticas. Quando você usa a busca de GIFs, o texto da busca e os dados necessários à solicitação são enviados ao Tenor; o operador deve manter a chave da API no servidor e exibir a atribuição exigida pelo Tenor quando os resultados forem mostrados. O operador também deve informar seus provedores de hospedagem, logs e backups, caso existam.

### Seus direitos

Você pode solicitar confirmação de tratamento, acesso, correção, eliminação e outros direitos previstos na LGPD ao controlador **[OPERATOR NAME]**, pelo contato **[OPERATOR PRIVACY CONTACT]**. O operador poderá pedir informações razoáveis para confirmar a identidade e deverá responder conforme a LGPD e as limitações legais aplicáveis.

---

## 1. Scope and template status

This notice describes the declared Aerochat data contract for a self-hosted deployment. It is not a promise that every operator configures the same infrastructure. Before going live, the operator must update it for actual hosting, logs, backups, subprocessors, retention schedules, legal bases, international transfers, and contact channels.

This notice is designed with Brazil's Lei Geral de Proteção de Dados Pessoais (LGPD), Law No. 13.709/2018, in mind. The LGPD covers processing of personal data, including in digital environments, and identifies the controller as the person or entity making decisions about processing.[8] This document is operational guidance, not legal advice or a determination of the operator's legal bases.

## 2. Controller and contacts

| Role | Deployment-specific value |
| --- | --- |
| Controller/operator | **[OPERATOR NAME]** |
| Service URL | **[OPERATOR SERVICE URL]** |
| Privacy contact | **[OPERATOR PRIVACY CONTACT]** |
| LGPD encarregado/DPO, if appointed | **[OPERATOR ENCARREGADO CONTACT]** |
| Hosting region(s) | **[OPERATOR HOSTING REGION]** |
| Retention and purge policy | **[OPERATOR RETENTION POLICY]** |

The operator is responsible for deciding the purposes and means of processing, publishing a current notice, responding to data-subject requests, securing the deployment, and documenting the legal basis selected for each purpose. The maintainers of the source repository do not control a private operator's database, hosting account, logs, OAuth registrations, or backups.

## 3. What the service stores

### Account identity

The OAuth-only account record is keyed by the provider and provider user ID. The declared profile fields are:

- provider identifier, such as Google, GitHub, or Discord;
- provider user ID;
- display name; and
- optional email address, stored only when the provider response explicitly marks the email as verified (`email_verified`/equivalent provider signal).

An email address is optional profile metadata, not the account key and not a password-recovery identifier.

### Conversations and messages

The server stores conversation metadata, membership/participant relationships, and messages in SQLite. Message history may include message bodies, message kind/reference data, creation time, edit time, and deletion time. A message deletion is supported as a soft delete; the record can remain available to the operator until the configured purge process removes it.

### Authentication and session material

Aerochat has no password account system. It does not create or store passwords, password hashes, password-reset answers, or arbitrary user-supplied API tokens. Provider OAuth access/refresh tokens are used only during the server-side exchange and are not persisted as account credentials. The supported Aerochat session credential is a server-issued signed JWT. Provider client secrets belong in the operator's server configuration and must not be committed or logged; treat session tokens as secrets, do not put them in URLs or logs, and configure an appropriate signing key.

This statement does not prevent a provider, browser, operating system, reverse proxy, or operator log from processing its own authentication records. Those systems are outside the Aerochat database and must be described by the operator when applicable.

### Built-in telemetry

The visual-shell contract forbids telemetry, analytics, update checkers, crash uploaders, and hidden network behavior. An operator may still enable ordinary web-server, infrastructure, security, or backup logs. If those logs contain IP addresses, user agents, token fragments, request bodies, or other personal data, the operator must document them, restrict access, and set a retention period.

## 4. Purposes and legal bases

The operator may process the data above to:

1. authenticate a user through a selected OAuth provider and maintain an Aerochat session;
2. create and maintain the user's account and conversations;
3. store, deliver, edit, and soft-delete messages at the user's request;
4. protect the service, prevent abuse, troubleshoot failures, and enforce operator rules; and
5. comply with legal obligations or respond to lawful requests.

The operator must map each purpose to an appropriate LGPD legal basis, document that choice, and apply necessity, transparency, security, prevention, and accountability principles. Do not present this template as a blanket finding that a particular operator may rely on consent, contract, legitimate interests, or another basis.

## 5. Sources and third parties

### OAuth providers

The current account flow supports Google, GitHub, and Discord. A provider receives the requests needed for OAuth and returns the identity fields the operator has configured to use. Each provider has its own terms and privacy policy. The operator must publish the configured provider list and any provider-specific transfer or retention details.

Aerochat does not require a user to provide a password to the operator. Provider-side authentication, provider cookies, and provider records remain governed by the provider.

### Tenor GIF search

GIF search is an optional proxied feature. When a user searches for a GIF, the search query and request metadata needed for that search are sent to Tenor. Do not send an operator's Tenor API key to the desktop client. Tenor's API is governed by Google/Tenor terms rather than an open-source license; its terms prohibit altering or reordering results and require compliance with its policies.[6]

Tenor's documentation requires attribution for retrieved content and documents content filtering parameters.[7] The deployment must use a suitable safety/content-filter setting and render the applicable Tenor attribution where GIF results are shown. The operator should tell users that their GIF search terms leave the self-hosted server for this purpose.

### Hosting, storage, logs, and backups

SQLite is the application database format, but the operator controls the machine, volume, backup service, reverse proxy, email/support tooling, and observability stack. List those vendors and processing locations here before publication: **[OPERATOR SUBPROCESSORS AND HOSTING DETAILS]**. This bracketed value is an operator-maintained deployment field, not a promise from the source repository.

## 6. Retention and deletion

Retention is operator-configured rather than fixed by the source repository. The operator must publish **[OPERATOR RETENTION POLICY]**, including:

- how long active accounts and message history are kept;
- when soft-deleted messages are purged;
- how long backups, server logs, OAuth flow state, and session-related records remain;
- how legal holds or incident-response copies are handled; and
- how expired data is securely deleted from primary storage and backups.

A soft-deleted message is not necessarily erased immediately. Backups may retain an earlier copy until their normal rotation and purge cycle.

### Account deletion path

A `DELETE /me` endpoint is planned but is **not an available, guaranteed endpoint in the current contract**. Until it exists, the operator must provide a manual request channel at **[OPERATOR PRIVACY CONTACT]**, verify the requester, identify the account by its provider identity, and document what is deleted, anonymized, retained under a legal obligation, or preserved in a backup rotation. Operators should test their deletion process against authored messages, participant relationships, conversations, logs, and backups rather than claiming that a single database row is complete erasure.

## 7. Security responsibilities

The operator should, at minimum:

- serve OAuth callbacks, API traffic, and WebSockets over appropriately protected HTTPS/WSS infrastructure;
- protect OAuth client secrets, the session-signing key, database files, backups, and deployment credentials;
- restrict database and backup access and encrypt storage where appropriate;
- prevent tokens and message bodies from entering debug logs or error reports;
- keep dependencies and the host patched; and
- test authentication, authorization, deletion, backup restoration, and incident procedures.

The software's source boundaries do not replace an operator's security review. A self-hosted deployment can be misconfigured even when the source tests pass.

## 8. International transfers and user choices

Google, GitHub, Discord, Tenor, hosting providers, and backup services may process data outside Brazil or in regions selected by the operator. The operator must identify the relevant transfer, verify the safeguards and legal mechanism required by the LGPD, and explain it to users. If the operator disables Tenor or a provider, the notice and UI must reflect that choice.

Users should avoid placing passwords, government identifiers, health data, financial data, or other sensitive information in chat unless the operator has a documented reason and appropriate safeguards. Messages can be copied, forwarded, backed up, or retained under the operator's policy.

## 9. Requests, complaints, and changes

Send privacy requests to **[OPERATOR PRIVACY CONTACT]**. The operator may need to verify identity, protect another person's data, or apply a legal exception before completing a request. If a request cannot be completed, the operator should explain the reason and the available escalation path, including the relevant Brazilian authority where appropriate.

The operator must review this notice whenever it enables a new provider, API, log, storage system, data field, or retention rule. The source repository may change independently of the operator's deployment; a copied notice is not automatically current.

## Sources

[6] [Tenor API Terms of Service](https://developers.google.com/tenor/guides/api-terms)

[7] [Tenor API documentation: content filtering and attribution](https://tenor.com/gifapi/documentation)

[8] [Lei nº 13.709/2018 (LGPD), Planalto](https://www.planalto.gov.br/ccivil_03/_ato2015-2018/2018/lei/l13709.htm)
