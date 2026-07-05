// Tiny React-shaped primitives — zero deps, zero build. `h` mirrors React.createElement so components read
// like JSX and the eventual port is mechanical; `createStore` is a Zustand-shaped store; `watch` is a
// selector subscription (re-render a region only when its slice changes).

// h(tag, props?, ...children) -> HTMLElement. props: class, html (innerHTML), on<Event> handlers, value,
// checked/disabled (properties), dataset (object), title/type/etc (attributes). Children may be nodes,
// strings, arrays, or null/false (skipped). Mirrors createElement's (type, props, ...children) shape.
export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null || v === false) continue;
      if (k === "class") el.className = v;
      else if (k === "html") el.innerHTML = v;
      else if (k === "dataset") Object.assign(el.dataset, v);
      else if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2).toLowerCase(), v);
      else if (k === "value") el.value = v;
      else if (k === "checked" || k === "disabled" || k === "selected") el[k] = !!v;
      else el.setAttribute(k, v);
    }
  }
  for (const c of children.flat(Infinity)) {
    if (c == null || c === false) continue;
    el.append(c.nodeType ? c : document.createTextNode(String(c)));
  }
  return el;
}

// mount(container, node): replace container's children with node (or nodes). Used to (re)render a region.
export function mount(container, node) {
  container.replaceChildren(...(Array.isArray(node) ? node : [node]));
}

// Zustand-shaped store: getState / setState(patch|fn) / subscribe(fn). setState shallow-merges.
export function createStore(initial) {
  let state = initial;
  const subs = new Set();
  return {
    getState: () => state,
    setState(patch) {
      state = { ...state, ...(typeof patch === "function" ? patch(state) : patch) };
      subs.forEach((f) => f(state));
    },
    subscribe(fn) {
      subs.add(fn);
      return () => subs.delete(fn);
    },
  };
}

// watch(store, selector, onChange): call onChange(state) once now and whenever the selector's output changes.
// selector returns an ARRAY of slices, compared shallowly (===), so big objects (the tree) compare by
// reference — cheap. This is the vanilla stand-in for a React selector/useStore subscription.
export function watch(store, selector, onChange) {
  let prev = null;
  const run = (s) => {
    const next = selector(s);
    if (prev && next.length === prev.length && next.every((x, i) => x === prev[i])) return;
    prev = next;
    onChange(s);
  };
  store.subscribe(run);
  run(store.getState());
}
