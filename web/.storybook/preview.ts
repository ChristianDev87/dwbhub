import type { Preview } from "@storybook/react";
import "../src/styles/globals.css";
import "../src/lib/i18n";

const preview: Preview = {
  parameters: {
    controls: {
      matchers: { color: /(background|color)$/i, date: /Date$/i },
    },
    a11y: { config: { rules: [] } },
    backgrounds: {
      default: "light",
      values: [
        { name: "light", value: "hsl(0 0% 100%)" },
        { name: "dark", value: "hsl(222 47% 11%)" },
      ],
    },
  },
};

export default preview;
