# ZnakiAI: architektura

## Cel

Wtyczka BricsCAD, która na podstawie polecenia w języku naturalnym („zajęcie prawego pasa km 50–70, ustaw oznakowanie tymczasowe”) nanosi na rysunek oznakowanie pionowe i poziome. Ma działać szybko i precyzyjnie.

## Zasada nadrzędna: AI decyduje, geometria liczy

Model językowy **nigdy nie podaje współrzędnych**. Zwraca tylko plan w kilometrażu: kod znaku, km, stronę, odsunięcie i status. Współrzędne, obroty i kreski linii liczy deterministyczny silnik geometrii w C#. Dzięki temu:

- precyzja nie zależy od AI: wynik jest powtarzalny co do 0,5 mm (tolerancja dyskretyzacji łuków),
- każdy plan przechodzi walidację (kody z katalogu, zakres osi, odsunięcia, kolizje słupków), zanim cokolwiek trafi do rysunku,
- plan zapisuje się jako JSON obok DWG. Można go poprawić ręcznie i wstawić ponownie komendą `ZNAKIPLAN`, już bez AI.

```
 Projektant ──polecenie──▶ ZNAKIAI (BricsCAD)
                             │ 1. wskazanie osi (krawędź jezdni) → Alignment (km)
                             │ 2. zebranie istniejących znaków w pobliżu osi → km/odsunięcie
                             ▼
                        AiPlanner ──1 wywołanie, JSON wg schematu──▶ Claude API
                             │◀──────────── PlacementPlan (km, kody) ──────┘
                             ▼
                        PlanResolver: walidacja + geometria (współrzędne, obroty, kreski)
                             ▼
                        lista w linii komend + podgląd na rysunku → [Tak] zatwierdza / [Nie] wycofuje
                             ▼
                        1 transakcja: bloki + atrybuty, odnośniki, linie o stałej szerokości
```

## Moduły

| Projekt | Zależności | Zawartość |
|---|---|---|
| `ZnakiAI.Core` | tylko .NET 8 + SDK Anthropic | geometria (`Alignment`), katalog, plan, walidacja, warstwa AI, konfiguracja. Testowalny bez BricsCAD. |
| `ZnakiAI.Bricscad` | `BrxMgd.dll`, `TD_Mgd.dll` (BricsCAD V25/V26) | komendy, odczyt osi z obiektów CAD, zapis do rysunku, import bloków z biblioteki DWG |
| `ZnakiAI.Core.Tests` | xUnit | testy precyzji (łuki, linie równoległe, kreski, obroty, walidacja) |

## Szybkość

- **Jedno wywołanie AI na polecenie.** Bez pętli narzędzi, odpowiedź wymuszona schematem JSON (structured output), więc nie trzeba niczego poprawiać ani ponawiać.
- **Cache promptu.** Instrukcje i katalog znaków to stały prefiks z `cache_control`. Kolejne wywołania czytają go z cache, co jest szybsze i tańsze.
- **Effort konfigurowalny.** `low` daje najszybszą odpowiedź do prostych poleceń, `medium` jest domyślny, `high` służy do złożonych skrzyżowań.
- **Zapis w jednej transakcji.** Setki kresek i znaków wstawiają się w milisekundach, a jedno `U` cofa całość.
- Do AI trafia zwięzły kontekst: liczby względem osi, bez całego rysunku.

## Precyzja

- Łuki polilinii (bulge) są zamieniane na cięciwy ze strzałką ≤ 0,5 mm. Kilometraż liczony jest po łuku.
- Linie równoległe liczone są przez przecięcie przesuniętych odcinków (miter), a nie przez przybliżenie.
- **Kreski linii przerywanych są mierzone po samej linii**, a nie po osi bazowej. Na łuku R = 50 m przy odsunięciu 3 m różnica wynosiłaby 6%.
- Linie są rysowane jako polilinie o stałej szerokości równej szerokości znaku (np. 0,12 m). Nie zależą od LTSCALE ani od typów linii.
- `ZNAKIKM` pokazuje km i odsunięcie dowolnego punktu względem osi, co służy do kontroli.

## Konwencje

- Kierunek osi = kierunek rysowania obiektu. Km 0 to jego początek.
- Znak pionowy: `strona` Prawa/Lewa względem kierunku osi, `odsuniecie` ≥ 0 to odległość słupka od osi.
- Linia: `odsuniecie` ze znakiem (+ lewo, − prawo) określa położenie osi linii.
- Symbol znaku stoi `odlegloscSymbolu` m od słupka, na zewnątrz jezdni, z odnośnikiem. Tak samo wyglądają plany COR (np. Grunwaldzka 43).
- Statusy (proj / tymczas / ist / do usunięcia) trafiają na osobne warstwy i do atrybutu STATUS.

## Kolejne etapy (propozycja)

1. Rozpoznawanie krawędzi jezdni z rysunku: AI wybiera oś z warstw / obiektów zamiast wskazywania ręcznego.
2. Znaki poziome powierzchniowe: przejścia P-10, linie zatrzymania P-12, strzałki P-8, powierzchnie wyłączone P-21.
3. Szablony schematów COR (np. zajęcie pasa, zwężenie, ruch wahadłowy) jako gotowe plany do doboru przez AI.
