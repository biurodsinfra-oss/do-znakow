# ZnakiAI dla BricsCAD

Wtyczka .NET do BricsCAD, która nanosi oznakowanie pionowe i poziome na podstawie polecenia słownego (AI: Claude). Geometria jest liczona deterministycznie, więc AI decyduje *co i gdzie w kilometrażu*, a program liczy *dokładne współrzędne*. Szczegóły: [docs/ARCHITEKTURA.md](docs/ARCHITEKTURA.md).

## Komendy

| Komenda | Działanie |
|---|---|
| `ZNAKIAI` | wskaż oś (krawędź jezdni), opcjonalnie strefę robót, wpisz polecenie. AI planuje, wtyczka waliduje i pokazuje podgląd na rysunku. „Tak” zatwierdza, „Nie” wycofuje bez śladu. Po zatwierdzeniu zapisuje zestawienie CSV (sztuki znaków, m² linii) i opcjonalnie wstawia je jako tabelę. Plan zapisuje się jako JSON obok DWG. |
| `ZNAKIPLAN` | wstawia plan z pliku JSON (bez AI). Służy do ręcznych poprawek i powtarzalnych schematów. |
| `ZNAKIKM` | kontrola: km i odsunięcie wskazanego punktu względem osi. |
| `ZNAKIKATALOG` | lista kodów znaków i linii z katalogu. |
| `ZNAKIOS` | wskazanie osi dla Claude (MCP). |
| `ZNAKIMCP` | włącza / wyłącza most MCP. |

**Claude Desktop / Claude Code (MCP):** Claude może sterować wtyczką bezpośrednio, bez klucza API. Instrukcja: [docs/MCP.md](docs/MCP.md).

Przykładowe polecenie dla `ZNAKIAI`:

> Zajęcie prawego pasa ruchu na czas robót w strefie. Oznakuj tymczasowo: ostrzeżenie o robotach, zakaz zatrzymywania, nakaz omijania, linia krawędziowa wzdłuż zajęcia.

## Instalacja (Windows)

1. Zainstaluj .NET 8 SDK.
2. Build:
   ```
   dotnet build src/ZnakiAI.Bricscad -c Release -p:BricsCADDir="C:\Program Files\Bricsys\BricsCAD V26 en_US"
   ```
3. Ustaw zmienną środowiskową `ANTHROPIC_API_KEY` (klucz z console.anthropic.com).
4. W BricsCAD: `NETLOAD`, potem `src/ZnakiAI.Bricscad/bin/Release/net8.0-windows/ZnakiAI.Bricscad.dll`.
   Autoładowanie: dodaj DLL do `APPLOAD` → *Startup Suite*.
5. `bibliotekaDwg` w `znakiai.config.json` (obok DLL) wskazuje domyślnie folder `C:\ProgramData\GACAD`. Może to być folder albo pojedynczy plik DWG. W folderze wtyczka szuka pliku nazwanego kodem znaku (`A-14.dwg`), a jeśli go nie ma, bloku o tej nazwie wewnątrz plików DWG. Pisownię porównuje luźno. Skąd pochodzi każdy blok, widać w linii komend. Znaki, których nie znajdzie, dostają prosty blok zastępczy.

## Konfiguracja: `znakiai.config.json`

- `model`: domyślnie `claude-opus-5-5`.
- `effort`: `low` (najszybciej), `medium` (domyślnie) lub `high`.
- `skalaSymbolu`, `odlegloscSymbolu`: wielkość symbolu i jego odsunięcie od słupka.
- warstwy dla statusów proj / tymczas / ist.

Wywołania AI mają włączony serwerowy fallback (`server-side-fallback`): gdy model odmówi, API samo przełącza na model zapasowy. Wyłączysz go przez `AiSettings.ServerFallback = false`.

## Katalogi: do uzupełnienia/weryfikacji

- `catalog/znaki_pionowe.json`: startowy zestaw znaków A/B/C/D/T/U. Pole `blok` mapuje kod na nazwę bloku w Twojej bibliotece.
- `catalog/znaki_poziome.json`: **wymiary kresek/przerw to wartości startowe z flagą `zweryfikowane: false`**. Sprawdź je z załącznikiem nr 2 do rozporządzenia (Dz.U. 2019 poz. 2311) i ustaw `true`. Do tego czasu wtyczka wyświetla ostrzeżenie przy każdej linii.

## Testy (bez BricsCAD)

```
dotnet test tests/ZnakiAI.Core.Tests
```

Testy sprawdzają kilometraż i odsunięcie na prostej, łuki z bulge (strzałka ≤ 0,5 mm), linie równoległe na łuku, wzór kresek, obroty symboli, walidację planu i zgodność JSON ze schematem AI.
