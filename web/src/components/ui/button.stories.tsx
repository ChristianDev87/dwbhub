import type { Meta, StoryObj } from "@storybook/react";
import { Button } from "./button";

const meta: Meta<typeof Button> = {
  title: "UI/Button",
  component: Button,
  tags: ["autodocs"],
  args: { children: "Klick mich" },
};

export default meta;

type Story = StoryObj<typeof Button>;

export const Default: Story = {};

export const Outline: Story = { args: { variant: "outline" } };

export const Ghost: Story = { args: { variant: "ghost" } };

export const Destructive: Story = { args: { variant: "destructive", children: "Löschen" } };

export const Link: Story = { args: { variant: "link", children: "Weiter lesen" } };

export const Sizes: Story = {
  render: () => (
    <div className="flex items-center gap-3">
      <Button size="sm">Small</Button>
      <Button size="default">Default</Button>
      <Button size="lg">Large</Button>
    </div>
  ),
};

export const RenderAsAnchor: Story = {
  args: {
    render: <a href="https://example.com" rel="noreferrer" target="_blank" />,
    children: "External link",
  },
};

export const Disabled: Story = { args: { disabled: true } };
