import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createDOMRenderer, FluentProvider, RendererProvider } from '@fluentui/react-components';
import { App } from './App';
import { useThemeChoice } from './theme';
import './styles.css';

// Fluent UI 2 with the TransactHub brand theme, light or dark (ADR 0003). Fluent writes its styles into style elements;
// the page's content security policy allows only those that carry the nonce the server put in this page.
const nonce = document.querySelector('meta[name="th-style-nonce"]')?.getAttribute('content') || undefined;
const renderer = createDOMRenderer(document, nonce ? { styleElementAttributes: { nonce } } : {});

function Root() {
  const { choice, mode, theme, setChoice } = useThemeChoice();
  return (
    // The provider's class goes on every portal too, so the page's own box is a child of it.
    <FluentProvider theme={theme} className={`th-${mode}`}>
      <div className="th-root">
        <App themeChoice={choice} setThemeChoice={setChoice} />
      </div>
    </FluentProvider>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <RendererProvider renderer={renderer} targetDocument={document}>
      <Root />
    </RendererProvider>
  </StrictMode>,
);
