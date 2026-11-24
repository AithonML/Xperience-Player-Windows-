# Jerarquía de Ramas y Flujo de Trabajo (Gitflow)

1. **Ramas Protegidas (`main` y `develop`):**
   - `main` es intocable (solo producción).
   - `develop` es la rama base de integración, pero **NUNCA escribas código directamente sobre `develop`**.

2. **Ramas de Trabajo para Agentes (Obligatorio):**
   - Antes de iniciar cualquier tarea, muévete a `develop` (`git checkout develop && git pull origin develop`) y crea una rama nueva descriptiva según el tipo de tarea:
     - `feat/<nombre>`: Nuevas funcionalidades (ej. `feat/equalizer-ui`).
     - `fix/<nombre>`: Corrección de errores encontrados durante el desarrollo en `develop`.
     - `chore/<nombre>`: Configuración de entorno, dependencias, CI/CD o documentación (ej. `chore/env-setup`).
   - Al terminar la tarea y verificar que compila, fusiona la rama hacia `develop` (`git checkout develop && git merge --no-ff <rama>`) y elimina la rama temporal.

3. **Excepción de Emergencia (`hotfix/*`):**
   - Solo cuando se indique explícitamente un **hotfix de producción**, parte desde `main`, crea `hotfix/vX.Y.Z`, aplica el arreglo y haz *merge* tanto a `main` como a `develop` (backport).
