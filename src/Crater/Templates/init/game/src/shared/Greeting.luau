local Greeting = {}

function Greeting.hello(name: string): string
	return `Hello from the {name}`
end

return Greeting
