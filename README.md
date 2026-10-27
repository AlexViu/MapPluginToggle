# MapPluginToggle

Plugin de [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) para CS2 que permite
**desactivar otros plugins en mapas concretos**, desde un menú WASD dentro del juego.

Entras en un mapa, abres `!mapplugins`, ves todos los plugins cargados con su estado y activas/desactivas los
que quieras. La elección se guarda en un JSON y se aplica automáticamente cada vez que se vuelve a jugar ese mapa.

## Requisitos

- CounterStrikeSharp API **1.0.378** o compatible (compilado para `net10.0`)
- [CS2MenuManager](https://github.com/schwarper/CS2MenuManager) instalado en `addons/counterstrikesharp/shared/`

## Instalación

1. Descarga `MapPluginToggle.zip` de la [última release](../../releases/latest) y descomprímelo en
   `addons/counterstrikesharp/plugins/`. Te queda `plugins/MapPluginToggle/MapPluginToggle.dll`.
2. Reinicia el servidor o cárgalo en caliente con `css_plugins load MapPluginToggle`.
3. El plugin crea `configs/plugins/MapPluginToggle/` (config + `maps.json`). Si el servidor corre con un usuario
   que no puede escribir en `configs/plugins`, crea esa carpeta a mano con el dueño correcto.

## Comandos

Por defecto requieren el permiso `@css/root` (configurable).

| Comando | Qué hace |
|---|---|
| `!mapplugins` | Menú WASD con todos los plugins y su estado en el mapa actual; elegir uno lo activa/desactiva y el menú se reabre en la misma posición |
| `css_mapplugins list` | Lista numerada (útil por RCON/consola) |
| `css_mapplugins toggle <carpeta\|#n>` | Activa/desactiva ese plugin en el mapa actual |
| `css_mapplugins reload` | Relee `maps.json` y lo aplica ya |

Estados: `ON`, `OFF`, `OFF por surf_*` (desactivado por patrón), `OFF, aún cargado` / `ON, no cargado` (lo
deseado no coincide con lo real, p. ej. un plugin que falló al cargar).

## Configuración

`configs/plugins/MapPluginToggle/MapPluginToggle.json`:

```json
{
  "Permission": "@css/root",
  "ChatPrefix": " \u0004[MapPlugins]\u0001"
}
```

`configs/plugins/MapPluginToggle/maps.json`:

```json
{
  "Maps": {
    "de_dust2": [ "RespawnKiller", "afkmanager" ],
    "surf_*":   [ "RespawnKiller" ]
  }
}
```

- Clave = nombre de mapa (sin distinguir mayúsculas) o patrón con `*`.
- Valor = nombres de **carpeta** de plugin (`plugins/<Carpeta>/<Carpeta>.dll`).
- El menú solo edita la clave exacta del mapa actual; los patrones se editan a mano + `css_mapplugins reload`.

## Cómo funciona

- Al empezar cada mapa (`OnMapStart`, diferido al siguiente frame) descarga los plugins listados para ese mapa y
  vuelve a cargar los que estén listados en *otros* mapas pero no en este. Los plugins que no aparecen en
  `maps.json` nunca se tocan.
- Cargar/descargar nunca se hace dentro del callback de un comando, menú o listener: siempre se difiere al
  siguiente frame para no modificar las listas de callbacks mientras se recorren.
- La recarga usa `Load(hotReload: true)`, igual que el hot-reload del núcleo, para que el plugin se inicialice
  como si estuviera a mitad de mapa.
- CounterStrikeSharp no expone su `IPluginManager` a los plugins, así que se obtiene por reflexión de
  `Application._pluginManager`. Si una versión futura de CSS lo cambia, el plugin lo avisa en el log y no hace
  nada.

## ⚠️ Advertencias

- **No todos los plugins se pueden descargar en caliente.** Un plugin que registra hooks nativos y no los quita
  en su `Unload` hará crashear el servidor después de descargarlo
  (`A callback was made on a garbage collected delegate`). Se ha visto con **SharpTimer**. Prueba cada plugin
  con el servidor vacío antes de usarlo con jugadores.
- No desactives plugins de los que dependen otros (librerías de menús, plugins que exponen capacidades
  compartidas…): los dependientes pueden fallar.
- Si descargas a mano (`css_plugins unload`) un plugin que aparece en `maps.json`, se recargará en el siguiente
  cambio de mapa si ese mapa no lo desactiva.

## Compilar

```
dotnet build -c Release
```

Salida: `bin/Release/net10.0/MapPluginToggle.dll`. `CounterStrikeSharp.API` y `CS2MenuManager` se referencian
como paquetes NuGet solo para compilar; no se copian a la salida porque el servidor ya los proporciona.
