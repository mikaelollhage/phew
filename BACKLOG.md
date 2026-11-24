# PHEW! --- Development backlog

**Family life, sorted.**

Senast sammanställd: 2026-11-24

Detta är en levande backlogg för PHEW! (tidigare Receptbanken). Den bygger på våra utvecklingsdiskussioner och rapporterade implementationer, inte en ny granskning av repot. Prioriteringarna nedan är förslag där inget annat beslutats.

## Arbetsprinciper

-   Hydrogen implementerar och testar. Argon granskar skrivskyddat vid behov.
-   Funktionalitet före kosmetiska förbättringar. Mobile-first, ljust och mörkt tema.
-   Inga orelaterade ändringar eller automatisk deploy i utvecklingsuppdrag.
-   Uppdatera denna fil när en uppgift påbörjas, avslutas eller ändrar omfattning.
-   TODO-UX-1 tas sist bland de aktiva UI-projekten.

## Fas 0 --- Namnbyte och produktionsövergång

-   [x] PHEW-RENAME-0 --- Argons inventering av namnbyte, backup och produktionsrisker.
-   [x] PHEW-RENAME-1 --- Engångsstädning av ZIP, borttagning av Test och beslut om strikt backupidentifierare Phew. Användaren rapporterar lyckad testimport.
-   [x] PHEW-RENAME-2 --- Fullständigt namnbyte av .NET, UI, PWA, Docker och deploy/rollback. Hydrogen rapporterar 1 008 godkända tester, inga byggvarningar och lyckad dry-run.
-   [x] GitHub-repo phew och SSH-remote konfigurerade. SSH-problemet rapporterat löst.
-   [x] Senaste commit pushad och produktionsövergången på servern slutförd (`/opt/phew`, `phew-data`, `phew.db`).
-   [x] Ny installation: `/opt/phew`, `phew-data`, `phew.db`, `phew.ollhage.home`.
-   [x] Kontrollera `.deploy.env`, ledig host-port, Docker-bygge och healthcheck.
-   [x] Återställ förberedd ZIP med `application: Phew`, `formatVersion: 1`.
-   [x] Verifiera data, bilder, betyg, kalender, veckomenyer och att Test är borta.
-   [x] DNS/BIND9, Caddy/HTTPS, PWA och pushprenumerationer.
-   [x] Uppdatera eventuella externa genvägar till röst-API:t.
-   [x] Bevara gamla Receptbanken och dess volym/backuper tills separat beslut om städning.

**Status:** Applikationens namnbyte och servermigrering är genomförda. Efterföljande branding- och temaändringar (`BRAND-1`, `BRAND-1F`, `BRAND-1.1`) är implementerade lokalt och inväntar visuell granskning samt separat produktionsdeploy.

## Fas 1 --- Funktionella förbättringar

### SHOP-IMPORT-3 --- Konflikter med befintlig inköpslista

-   [ ] Utforma säker hantering när importerade ingredienser redan finns på inköpslistan.
-   Undvik tysta, felaktiga sammanslagningar eller förlust av mängder. Bevara befintligt beteende för mängdfria och mängdangivna rader. Exakta konfliktregler beslutas före implementation.

### SHOP-IMPORT-4 --- Familjens standardinställning

-   [ ] Spara familjens standardval för import med/utan mängder.
-   Behåll möjlighet till tillfälliga val och individuella radöverstyrningar. Mängdfritt är nuvarande standard.

### FAM-1C --- Kalenderdeltagare

-   [ ] Utred kvarvarande behov kring familjemedlemmar och kalenderdeltagare.
-   Omfattningen är ännu inte fastställd. Återinför inte inaktiva familjemedlemmar.

### CAL-3 --- Medlemsfilter i kalendern

-   [ ] Planera filter för kalenderhändelser per familjemedlem.
-   Besluta hur gemensamma händelser, händelser utan deltagare och återkommande undantag ska visas.

### TODO-1C --- Jarvis REST

-   [ ] Planera REST-integration för att-göra-listor med Jarvis.
-   API-kontrakt, autentisering och omfattning behöver fastställas innan implementation.

## Fas 2 --- Mindre UX-förbättringar

### UX-ING-1 --- Ingrediensradens markering

-   [x] Förhindra att hela ingrediensraden lyser upp när trepunktsmenyn öppnas (implementerat i `BRAND-1.1` / B07; inväntar visuell verifiering i webbläsare).
-   Endast menyknappen ska ge visuell respons. Radens bakgrund ska vara oförändrad.
-   Bevara avbockningsmarkering, menyfunktion, placering och synligt tangentbordsfokus.
-   Faktisk orsak åtgärdad: `:not(:has(...))` på rad-hover i `app.css`, rensning av kvarstående dragstatus i `ToggleRowMenu`, samt `:focus:not(:focus-visible)` på menyknappar; verifierat med bUnit-test.
-   Kvarstår: visuell kontroll i riktig webbläsare på mobil samt i ljust och mörkt tema.

### UX-2 --- Kompaktare notiser

-   [ ] Se över presentationen av notiser så att den tar mindre plats.
-   Exakt utformning beslutas senare.

### PWA-ICON --- PHEW!-ikoner

-   [x] Ta fram och implementera ny ikonografi för PHEW! (implementerat i `BRAND-1`; inväntar visuell granskning och produktionsdeploy).
-   `/favicon.svg`, `/favicon.ico`, `/icons/icon-192.png`, `/icons/icon-512.png`, fullbleed `/icons/icon-maskable-512.png` och `/apple-touch-icon.png` integrerade i `manifest.webmanifest` och `App.razor`; gamla `favicon.png` borttagen.

## Fas 3 --- Uppskjuten UX-granskning (UX-1B)

Följande är tidigare rapporterade observationspunkter från Argon. Kontrollera om de fortfarande gäller innan implementation. De är inte verifierade mot den omdöpta kodbasen.

-   [ ] SVG-ikoner i inställningar: risk för krympning.
-   [ ] Notisbadge i mörkt tema: kontrast och odefinierad CSS-variabel.
-   [ ] Favoritknapp: ARIA och tryckyta.
-   [ ] Synliga focus-visible-markeringar.
-   [ ] Dialoger: Escape och fokushantering.
-   [ ] Att-göra: radering utan ångra (samordnas med TODO-UX-1).
-   [ ] Att-göra: emoji-knappar (samordnas med TODO-UX-1).
-   [ ] Att-göra: ARIA och tabbhantering (samordnas med TODO-UX-1).
-   [ ] Receptdetalj: hero och storlek på raderingsknapp.
-   [ ] Receptimport/redigering: responsiva åtgärdsknappar.
-   [ ] Portionsval: layout vid valideringsfel.
-   [ ] För små tryckytor.
-   [ ] Inkonsekventa ikoner och stilar.
-   [ ] Odefinierade CSS-variabler.

## Fas 4 --- TODO-UX-1 (sist bland aktiva UI-projekt)

-   [ ] Samlad UX-översyn av att-göra-listorna.
-   Samordna de uppskjutna Argon-punkterna om radering, emoji-knappar och tillgänglighet.
-   Fastställ konkret design och acceptanskriterier innan Hydrogen börjar.

## Genomfört --- referens

-   [x] BRAND-1 --- Ny visuell identitet för PHEW! med grön färgpalett (`#397B4D` / `#8BCB91` / `#174638`), `phew-logo.webp`, ny `favicon.svg` / `favicon.ico`, PWA-ikoner (`192`, `512`, `maskable-512`, `apple-touch-icon`) och uppdaterad `manifest.webmanifest` (implementerat; inväntar slutlig visuell godkännande & produktionsdeploy).
-   [x] BRAND-1F --- Förstklassigt tema för Ljust, Mörkt och System med lokal lagring (`localStorage['phew.theme']`), synkron flimmerfri uppstart i `App.razor`, `AppThemeService`, `FluentDesignTheme`-synk och utseendeväljare på `/installningar` (implementerat; inväntar slutlig visuell godkännande & produktionsdeploy).
-   [x] BRAND-1.1 --- Visuell puts och färgkonsistens (B01–B12, inklusive `UX-ING-1`): gröna val- och drag-och-släpp-stilar i inköpsimport/recept/veckomeny, gröna sektionsrubriker, neutrala vardagsdatum, enhetlig grön "IDAG"-markering, högkontrast kalendertidsbadge i ljust tema, åtgärdad radmarkering vid trepunktsmeny (`UX-ING-1`), full illustrerad logotyp i desktop-sidopanel med kompakt mobilheader, rent tangentbordsfokus utan dubbelram i navigering, ljusare vita sekundärknappar i ljust tema, fönsterliknande temaförhandsvisningar och diskret "Om PHEW!"-kort i Inställningar (implementerat; inväntar ägarens visuella godkännande & produktionsdeploy).
-   [x] FAM-1B / FAM-1B.1 --- Familjeinställningar.
-   [x] RATE-1B / RATE-1C / RATE-1C.1 --- Receptbetyg inklusive Bayesiansk dämpning.
-   [x] SHOP-IMPORT-1 / SHOP-IMPORT-2 --- Mängdfri import som standard, globalt val och radöverstyrningar, svensk A--Ö-sortering, konservativ gruppering och gemensamma hjälpfunktioner för recept/veckomenyer.
-   [x] SHOP-IMPORT-2.1 --- Ikonbaserade mängdval, varning endast vid faktiska enhetskonflikter, mindre visuellt brus.
-   [x] UX-WEEK-2.5 --- Responsiv header på veckomenyns dagskort; kontroller hålls på samma rad.
-   [x] Förbättringar av hem- och veckomeny-UI.
-   [x] Kalender: större redigerings- och raderingsknappar på mobil.

**Nuvarande verifieringsbaslinje (efter BRAND-1.1):** `1 020` godkända tester (`0` misslyckade, `0` hoppade), `0` byggvarningar i Release-konfiguration och godkänd deploy dry-run (`deploy.ps1 -DryRun -SkipTests`).

**Kvarstår före produktion:** Implementation av `BRAND-1`, `BRAND-1F`, `BRAND-1.1`, `UX-ING-1` och `PWA-ICON` är slutförd i kod och tester, men visuell granskning i riktig webbläsare (bl.a. full desktop-logotyp i B08, "Om PHEW!" i B12 och responsivitet vid 320–430 px då webbläsarverktyg saknades i sessionen) samt produktionsdeploy återstår efter ägarens godkännande.

## Avsiktligt bortvalda uppgifter

-   Bakåtkompatibilitet för ZIP-manifest med application: Receptbanken. PHEW! accepterar endast Phew.
-   Gränssnitt för inaktiva familjemedlemmar och permanent legacy-städning. Den aktuella posten hanterades i backupen.
-   Direkt migrering eller namnbyte av gamla produktionsvolymen. Ny installation och ZIP-restore valdes istället.

## Ändringslogg

-   2026-11-24: Uppdaterad efter BRAND-1, BRAND-1F och BRAND-1.1. Fas 0 (namnbyte och servermigrering) markerad som slutförd. BRAND-1, BRAND-1F, BRAND-1.1, UX-ING-1 och PWA-ICON markerade som implementerade (baslinje: 1 020 godkända tester), med tydlig åtskillnad mot kvarstående visuell granskning och produktionsdeploy.
-   2026-11-24: Första samlade backloggen. UX-ING-1 tillagd. Namnbytesstatus och kvarvarande serververifiering dokumenterade.
