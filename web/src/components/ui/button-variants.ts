import { cva, type VariantProps } from "class-variance-authority";

/**
 * Tailwind variant definitions for the `<Button>` component.
 *
 * Lives in its own module (no JSX) so the matching `button.tsx` file can stay
 * a pure component module — required by the `react-refresh/only-export-components`
 * ESLint rule, which trips when a component file also exports non-components.
 *
 * Imported by:
 *   - components/ui/button.tsx (the Button component)
 *   - any consumer that wants the raw class string without rendering a button
 */
export const buttonVariants = cva(
  "inline-flex items-center justify-center gap-2 rounded-md text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:pointer-events-none disabled:opacity-50",
  {
    variants: {
      variant: {
        default: "bg-primary text-primary-foreground hover:bg-primary/90",
        outline:
          "border border-border bg-transparent hover:bg-muted text-foreground",
        ghost: "hover:bg-muted text-foreground",
        destructive: "bg-red-600 text-white hover:bg-red-700",
        link: "text-primary underline-offset-4 hover:underline",
      },
      size: {
        default: "h-10 px-4 py-2",
        sm: "h-8 rounded px-3 text-xs",
        lg: "h-11 rounded-md px-8",
        icon: "h-10 w-10",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  },
);

export type ButtonVariantProps = VariantProps<typeof buttonVariants>;
